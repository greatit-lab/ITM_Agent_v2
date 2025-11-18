// ITM_Agent/ucPanel/ucOptionPanel.Designer.cs
namespace ITM_Agent.ucPanel
{
    partial class ucOptionPanel
    {
        /// <summary>필수 디자이너 변수</summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>Debug Mode 체크박스</summary>
        private System.Windows.Forms.CheckBox chk_DebugMode;

        /// <summary>
        /// 사용 중 리소스 정리
        /// </summary>
        /// <param name="disposing">관리되는 리소스 해제 여부</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                // 타이머 리소스 정리 추가
                if (statusRefreshTimer != null)
                {
                    statusRefreshTimer.Dispose();
                }
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// 디자이너 지원에 필요한 메서드 — 코드 수정 금지
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.statusRefreshTimer = new System.Windows.Forms.Timer(this.components);
            this.chk_DebugMode = new System.Windows.Forms.CheckBox();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.label5 = new System.Windows.Forms.Label();
            this.chk_PerfoMode = new System.Windows.Forms.CheckBox();
            this.label1 = new System.Windows.Forms.Label();
            this.groupBox2 = new System.Windows.Forms.GroupBox();
            this.label4 = new System.Windows.Forms.Label();
            this.cb_info_Retention = new System.Windows.Forms.ComboBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.chk_infoDel = new System.Windows.Forms.CheckBox();
            this.groupBox3 = new System.Windows.Forms.GroupBox();
            this.label6 = new System.Windows.Forms.Label();
            this.pbDbStatus = new System.Windows.Forms.PictureBox();
            this.lblDbHost = new System.Windows.Forms.Label();
            this.label7 = new System.Windows.Forms.Label();
            this.pbObjStatus = new System.Windows.Forms.PictureBox();
            this.lblObjHost = new System.Windows.Forms.Label();
            this.btnRefreshStatus = new System.Windows.Forms.Button();
            this.groupBox1.SuspendLayout();
            this.groupBox2.SuspendLayout();
            this.groupBox3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pbDbStatus)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pbObjStatus)).BeginInit();
            this.SuspendLayout();
            // 
            // statusRefreshTimer
            // 
            this.statusRefreshTimer.Enabled = true;
            this.statusRefreshTimer.Interval = 60000; // 1분 (60,000ms)
            this.statusRefreshTimer.Tick += new System.EventHandler(this.statusRefreshTimer_Tick);
            // 
            // chk_DebugMode
            // 
            this.chk_DebugMode.AutoSize = true;
            this.chk_DebugMode.Location = new System.Drawing.Point(505, 66);
            this.chk_DebugMode.Name = "chk_DebugMode";
            this.chk_DebugMode.Size = new System.Drawing.Size(15, 14);
            this.chk_DebugMode.TabIndex = 0;
            this.chk_DebugMode.UseVisualStyleBackColor = true;
            this.chk_DebugMode.CheckedChanged += new System.EventHandler(this.chk_DebugMode_CheckedChanged);
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.label5);
            this.groupBox1.Controls.Add(this.chk_PerfoMode);
            this.groupBox1.Controls.Add(this.label1);
            this.groupBox1.Controls.Add(this.chk_DebugMode);
            this.groupBox1.Location = new System.Drawing.Point(25, 21);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(624, 98);
            this.groupBox1.TabIndex = 19;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "● Logging Option";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(20, 66);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(143, 12);
            this.label5.TabIndex = 43;
            this.label5.Text = "• Enable Debug Logging";
            // 
            // chk_PerfoMode
            // 
            this.chk_PerfoMode.AutoSize = true;
            this.chk_PerfoMode.Location = new System.Drawing.Point(505, 31);
            this.chk_PerfoMode.Name = "chk_PerfoMode";
            this.chk_PerfoMode.Size = new System.Drawing.Size(15, 14);
            this.chk_PerfoMode.TabIndex = 42;
            this.chk_PerfoMode.UseVisualStyleBackColor = true;
            this.chk_PerfoMode.CheckedChanged += new System.EventHandler(this.chk_PerfoMode_CheckedChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(20, 33);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(226, 12);
            this.label1.TabIndex = 41;
            this.label1.Text = "• Enable System Performance Logging";
            // 
            // groupBox2
            // 
            this.groupBox2.Controls.Add(this.label4);
            this.groupBox2.Controls.Add(this.cb_info_Retention);
            this.groupBox2.Controls.Add(this.label3);
            this.groupBox2.Controls.Add(this.label2);
            this.groupBox2.Controls.Add(this.chk_infoDel);
            this.groupBox2.Location = new System.Drawing.Point(25, 137);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Size = new System.Drawing.Size(624, 90);
            this.groupBox2.TabIndex = 42;
            this.groupBox2.TabStop = false;
            this.groupBox2.Text = "● Data Retention Option";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(554, 61);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(33, 12);
            this.label4.TabIndex = 44;
            this.label4.Text = "days";
            // 
            // cb_info_Retention
            // 
            this.cb_info_Retention.FormattingEnabled = true;
            this.cb_info_Retention.Location = new System.Drawing.Point(471, 58);
            this.cb_info_Retention.Name = "cb_info_Retention";
            this.cb_info_Retention.Size = new System.Drawing.Size(77, 20);
            this.cb_info_Retention.TabIndex = 43;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(29, 61);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(287, 12);
            this.label3.TabIndex = 42;
            this.label3.Text = "→ Retention period (days), date-only comparison";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(20, 33);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(380, 12);
            this.label2.TabIndex = 41;
            this.label2.Text = "• Auto-delete dated files (Baseline + subfolders; by filename date)";
            // 
            // chk_infoDel
            // 
            this.chk_infoDel.AutoSize = true;
            this.chk_infoDel.Location = new System.Drawing.Point(505, 31);
            this.chk_infoDel.Name = "chk_infoDel";
            this.chk_infoDel.Size = new System.Drawing.Size(15, 14);
            this.chk_infoDel.TabIndex = 0;
            this.chk_infoDel.UseVisualStyleBackColor = true;
            // 
            // groupBox3
            // 
            this.groupBox3.Controls.Add(this.btnRefreshStatus);
            this.groupBox3.Controls.Add(this.lblObjHost);
            this.groupBox3.Controls.Add(this.pbObjStatus);
            this.groupBox3.Controls.Add(this.label7);
            this.groupBox3.Controls.Add(this.lblDbHost);
            this.groupBox3.Controls.Add(this.pbDbStatus);
            this.groupBox3.Controls.Add(this.label6);
            this.groupBox3.Location = new System.Drawing.Point(25, 239);
            this.groupBox3.Name = "groupBox3";
            this.groupBox3.Size = new System.Drawing.Size(624, 90);
            this.groupBox3.TabIndex = 43;
            this.groupBox3.TabStop = false;
            this.groupBox3.Text = "● Server Connection";
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(20, 30);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(69, 12);
            this.label6.TabIndex = 0;
            this.label6.Text = "• DataBase";
            // 
            // pbDbStatus
            // 
            this.pbDbStatus.BackColor = System.Drawing.Color.Gray;
            this.pbDbStatus.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.pbDbStatus.Location = new System.Drawing.Point(155, 28);
            this.pbDbStatus.Name = "pbDbStatus";
            this.pbDbStatus.Size = new System.Drawing.Size(16, 16);
            this.pbDbStatus.TabIndex = 1;
            this.pbDbStatus.TabStop = false;
            // 
            // lblDbHost
            // 
            this.lblDbHost.AutoSize = true;
            this.lblDbHost.Location = new System.Drawing.Point(186, 30);
            this.lblDbHost.Name = "lblDbHost";
            this.lblDbHost.Size = new System.Drawing.Size(70, 12);
            this.lblDbHost.TabIndex = 2;
            this.lblDbHost.Text = "Checking...";
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(20, 58);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(84, 12);
            this.label7.TabIndex = 3;
            this.label7.Text = "• Object Story";
            // 
            // pbObjStatus
            // 
            this.pbObjStatus.BackColor = System.Drawing.Color.Gray;
            this.pbObjStatus.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.pbObjStatus.Location = new System.Drawing.Point(155, 56);
            this.pbObjStatus.Name = "pbObjStatus";
            this.pbObjStatus.Size = new System.Drawing.Size(16, 16);
            this.pbObjStatus.TabIndex = 4;
            this.pbObjStatus.TabStop = false;
            // 
            // lblObjHost
            // 
            this.lblObjHost.AutoSize = true;
            this.lblObjHost.Location = new System.Drawing.Point(186, 58);
            this.lblObjHost.Name = "lblObjHost";
            this.lblObjHost.Size = new System.Drawing.Size(70, 12);
            this.lblObjHost.TabIndex = 5;
            this.lblObjHost.Text = "Checking...";
            // 
            // btnRefreshStatus
            // 
            this.btnRefreshStatus.Location = new System.Drawing.Point(471, 30);
            this.btnRefreshStatus.Name = "btnRefreshStatus";
            this.btnRefreshStatus.Size = new System.Drawing.Size(116, 40);
            this.btnRefreshStatus.TabIndex = 6;
            this.btnRefreshStatus.Text = "Refresh";
            this.btnRefreshStatus.UseVisualStyleBackColor = true;
            // 
            // ucOptionPanel
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.groupBox3);
            this.Controls.Add(this.groupBox2);
            this.Controls.Add(this.groupBox1);
            this.Name = "ucOptionPanel";
            this.Size = new System.Drawing.Size(676, 340);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.groupBox2.ResumeLayout(false);
            this.groupBox2.PerformLayout();
            this.groupBox3.ResumeLayout(false);
            this.groupBox3.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pbDbStatus)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pbObjStatus)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.GroupBox groupBox2;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.CheckBox chk_infoDel;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.ComboBox cb_info_Retention;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.CheckBox chk_PerfoMode;
        // 타이머 선언
        private System.Windows.Forms.Timer statusRefreshTimer;
        private System.Windows.Forms.GroupBox groupBox3;
        private System.Windows.Forms.Button btnRefreshStatus;
        private System.Windows.Forms.Label lblObjHost;
        private System.Windows.Forms.PictureBox pbObjStatus;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label lblDbHost;
        private System.Windows.Forms.PictureBox pbDbStatus;
        private System.Windows.Forms.Label label6;
    }
}
