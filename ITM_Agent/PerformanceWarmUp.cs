// ITM_Agent/PerformanceWarmUp.cs
using ConnectInfo;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ITM_Agent.Startup
{
    internal static class PerformanceWarmUp
    {
        public static void Run()
        {
            // 1) PDH 카운터 더미 호출 (OS 레지스트리 손상 방어 로직 추가)
            try
            {
                var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpu.NextValue();
                Thread.Sleep(100);
                cpu.NextValue();  // 유효 값 확보
            }
            catch (Exception)
            {
                // Windows 7 등에서 성능 카운터(Registry)가 손상되었을 경우 
                // 에이전트가 뻗지 않도록 에러를 무시하고 조용히 넘어갑니다.
            }

            // 2) DB 커넥션 풀 최소 1개 미리 오픈
            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();
                using (var conn = new NpgsqlConnection(cs))
                { 
                    conn.Open(); 
                }            // SELECT 1 불필요
            }
            catch { /* 로깅만 */ }
        }
    }
}
