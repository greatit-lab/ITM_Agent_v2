// ITM_Agent/Services/PerformanceDbWriter.cs
using ConnectInfo;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ITM_Agent.Services
{
    public sealed class PerformanceDbWriter
    {
        private readonly string eqpid;
        private readonly List<Metric> buf = new List<Metric>(1000);
        private readonly Timer timer;
        private readonly object sync = new object();
        private const int BULK = 60;
        private const int FLUSH_MS = 30_000;
        private static readonly LogManager logger = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
        private readonly EqpidManager eqpidManager;

        // [핵심 개선] 스레드 중첩을 막기 위한 플래그
        private int _isFlushing = 0;

        private PerformanceDbWriter(string eqpid, EqpidManager manager)
        {
            this.eqpid = eqpid;
            this.eqpidManager = manager ?? throw new ArgumentNullException(nameof(manager));
            PerformanceMonitor.Instance.RegisterConsumer(OnSample);
            timer = new Timer(_ => Flush(), null, FLUSH_MS, FLUSH_MS);
        }

        private static PerformanceDbWriter current;

        public static void Start(string eqpid, EqpidManager manager)
        {
            if (current != null) return;
            PerformanceMonitor.Instance.StartSampling();
            current = new PerformanceDbWriter(eqpid, manager);
        }

        public static void Stop()
        {
            if (current == null) return;
            PerformanceMonitor.Instance.StopSampling();
            current.Flush();
            current.timer.Dispose();
            PerformanceMonitor.Instance.UnregisterConsumer(current.OnSample);
            current = null;
        }

        private void OnSample(Metric m)
        {
            lock (sync)
            {
                buf.Add(m);
                if (buf.Count >= BULK)
                    Flush();
            }
        }

        private void Flush()
        {
            // [핵심 개선] 이미 DB 인서트가 진행 중이면, 타이머나 버퍼 초과로 인한 동시 다발적 쿼리 대기를 차단
            if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) == 1) return;

            try
            {
                List<Metric> batch;
                lock (sync)
                {
                    if (buf.Count == 0) return;
                    batch = new List<Metric>(buf);
                    buf.Clear();
                }

                string cs;
                try { cs = DatabaseInfo.CreateDefault().GetConnectionString(); }
                catch { logger.LogError("[Perf] ConnString 실패"); return; }

                try
                {
                    using (var conn = new NpgsqlConnection(cs))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction())
                        {
                            // eqp_perf 테이블 INSERT
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText =
                                    "INSERT INTO eqp_perf (eqpid, ts, serv_ts, cpu_usage, mem_usage, cpu_temp, gpu_temp, fan_speed) " +
                                    " VALUES (@eqp, @ts, @srv, @cpu, @mem, @cpu_temp, @gpu_temp, @fan_speed) " +
                                    " ON CONFLICT (eqpid, ts) DO NOTHING;";

                                var pEqp = cmd.Parameters.Add("@eqp", NpgsqlTypes.NpgsqlDbType.Varchar);
                                var pTs = cmd.Parameters.Add("@ts", NpgsqlTypes.NpgsqlDbType.Timestamp);
                                var pSrv = cmd.Parameters.Add("@srv", NpgsqlTypes.NpgsqlDbType.Timestamp);
                                var pCpu = cmd.Parameters.Add("@cpu", NpgsqlTypes.NpgsqlDbType.Real);
                                var pMem = cmd.Parameters.Add("@mem", NpgsqlTypes.NpgsqlDbType.Real);
                                var pCpuTemp = cmd.Parameters.Add("@cpu_temp", NpgsqlTypes.NpgsqlDbType.Real);
                                var pGpuTemp = cmd.Parameters.Add("@gpu_temp", NpgsqlTypes.NpgsqlDbType.Real);
                                var pFanSpeed = cmd.Parameters.Add("@fan_speed", NpgsqlTypes.NpgsqlDbType.Integer);

                                foreach (var m in batch)
                                {
                                    string clean = eqpid.StartsWith("Eqpid:", StringComparison.OrdinalIgnoreCase) ? eqpid.Substring(6).Trim() : eqpid.Trim();
                                    pEqp.Value = clean;

                                    var ts = new DateTime(m.Timestamp.Year, m.Timestamp.Month, m.Timestamp.Day, m.Timestamp.Hour, m.Timestamp.Minute, m.Timestamp.Second);
                                    pTs.Value = ts;

                                    var srv = TimeSyncProvider.Instance.ToSynchronizedKst(ts);
                                    srv = new DateTime(srv.Year, srv.Month, srv.Day, srv.Hour, srv.Minute, srv.Second);
                                    pSrv.Value = srv;

                                    pCpu.Value = (float)Math.Round(m.Cpu, 2);
                                    pMem.Value = Math.Round(m.Mem, 2);
                                    pCpuTemp.Value = Math.Round(m.CpuTemp, 1);
                                    pGpuTemp.Value = Math.Round(m.GpuTemp, 1);
                                    pFanSpeed.Value = m.FanRpm;

                                    cmd.ExecuteNonQuery();
                                }
                            }

                            // eqp_proc_perf 테이블 INSERT 로직 수정
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                // SQL 쿼리에 memory_commit_mb 컬럼 추가
                                cmd.CommandText =
                                    "INSERT INTO public.eqp_proc_perf (eqpid, ts, serv_ts, process_name, memory_usage_mb, shared_memory_mb, memory_commit_mb) " +
                                    " VALUES (@eqp, @ts, @srv, @proc_name, @mem_mb, @shared_mem_mb, @commit_mb) " +
                                    " ON CONFLICT (eqpid, ts, process_name) DO NOTHING;";

                                var pEqp = cmd.Parameters.Add("@eqp", NpgsqlTypes.NpgsqlDbType.Varchar);
                                var pTs = cmd.Parameters.Add("@ts", NpgsqlTypes.NpgsqlDbType.Timestamp);
                                var pSrv = cmd.Parameters.Add("@srv", NpgsqlTypes.NpgsqlDbType.Timestamp);
                                var pProcName = cmd.Parameters.Add("@proc_name", NpgsqlTypes.NpgsqlDbType.Varchar);
                                var pMemMb = cmd.Parameters.Add("@mem_mb", NpgsqlTypes.NpgsqlDbType.Integer);
                                var pSharedMemMb = cmd.Parameters.Add("@shared_mem_mb", NpgsqlTypes.NpgsqlDbType.Integer);
                                var pCommitMb = cmd.Parameters.Add("@commit_mb", NpgsqlTypes.NpgsqlDbType.Integer); // 신규 파라미터 추가

                                foreach (var m in batch)
                                {
                                    if (m.TopProcesses == null || m.TopProcesses.Count == 0) continue;

                                    foreach (var proc in m.TopProcesses)
                                    {
                                        string clean = eqpid.StartsWith("Eqpid:", StringComparison.OrdinalIgnoreCase) ? eqpid.Substring(6).Trim() : eqpid.Trim();
                                        pEqp.Value = clean;

                                        var ts = new DateTime(m.Timestamp.Year, m.Timestamp.Month, m.Timestamp.Day, m.Timestamp.Hour, m.Timestamp.Minute, m.Timestamp.Second);
                                        pTs.Value = ts;

                                        var srv = TimeSyncProvider.Instance.ToSynchronizedKst(ts);
                                        srv = new DateTime(srv.Year, srv.Month, srv.Day, srv.Hour, srv.Minute, srv.Second);
                                        pSrv.Value = srv;

                                        pProcName.Value = proc.ProcessName;

                                        pMemMb.Value = (int)proc.MemoryUsageMB;             // Private Working Set 
                                        pSharedMemMb.Value = (int)proc.SharedMemoryUsageMB; // Shared Memory
                                        pCommitMb.Value = (int)proc.CommitMemoryMB;         // Commit Size (신규)

                                        cmd.ExecuteNonQuery();
                                    }
                                }
                            }

                            tx.Commit(); // 모든 작업이 성공하면 커밋
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[Perf] Batch INSERT 실패: {ex.Message}");
                }
            }
            finally
            {
                // [핵심 개선] DB 작업 완료 후 락 해제
                Interlocked.Exchange(ref _isFlushing, 0); 
            }
        }
    }
}
