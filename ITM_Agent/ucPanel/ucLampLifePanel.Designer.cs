// ITM_Agent/ucPanel/ucLampLifePanel.Designer.cs
namespace ITM_Agent.ucPanel
{
    partial class ucLampLifePanel
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        private void InitializeComponent()
        {
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.lblInfo = new System.Windows.Forms.Label();
            this.btnManualCollect = new System.Windows.Forms.Button();
            this.lblLastCollect = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.chkEnable = new System.Windows.Forms.CheckBox();
            this.label1 = new System.Windows.Forms.Label();
            this.groupBox1.SuspendLayout();
            this.SuspendLayout();
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.lblInfo);
            this.groupBox1.Controls.Add(this.btnManualCollect);
            this.groupBox1.Controls.Add(this.lblLastCollect);
            this.groupBox1.Controls.Add(this.label4);
            this.groupBox1.Controls.Add(this.chkEnable);
            this.groupBox1.Controls.Add(this.label1);
            this.groupBox1.Location = new System.Drawing.Point(25, 21);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(624, 133);
            this.groupBox1.TabIndex = 0;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "● Lamp Life Collector Settings";
            // 
            // lblInfo
            // 
            this.lblInfo.AutoSize = true;
            this.lblInfo.ForeColor = System.Drawing.Color.Gray;
            this.lblInfo.Location = new System.Drawing.Point(40, 55);
            this.lblInfo.Name = "lblInfo";
            this.lblInfo.Size = new System.Drawing.Size(433, 12);
            this.lblInfo.TabIndex = 8;
            this.lblInfo.Text = "※ Runs UI Automation once at start, then syncs with DB every 1 hour.";
            // 
            // btnManualCollect
            // 
            this.btnManualCollect.Location = new System.Drawing.Point(458, 94);
            this.btnManualCollect.Name = "btnManualCollect";
            this.btnManualCollect.Size = new System.Drawing.Size(140, 21);
            this.btnManualCollect.TabIndex = 7;
            this.btnManualCollect.Text = "Collect Now (UI)";
            this.btnManualCollect.UseVisualStyleBackColor = true;
            this.btnManualCollect.Click += new System.EventHandler(this.btnManualCollect_Click);
            // 
            // lblLastCollect
            // 
            this.lblLastCollect.AutoSize = true;
            this.lblLastCollect.Location = new System.Drawing.Point(129, 98);
            this.lblLastCollect.Name = "lblLastCollect";
            this.lblLastCollect.Size = new System.Drawing.Size(28, 12);
            this.lblLastCollect.TabIndex = 6;
            this.lblLastCollect.Text = "N/A";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Font = new System.Drawing.Font("굴림", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(129)));
            this.label4.Location = new System.Drawing.Point(20, 98);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(103, 12);
            this.label4.TabIndex = 5;
            this.label4.Text = "• Last Collection:";
            // 
            // chkEnable
            // 
            this.chkEnable.AutoSize = true;
            this.chkEnable.Location = new System.Drawing.Point(250, 32);
            this.chkEnable.Name = "chkEnable";
            this.chkEnable.Size = new System.Drawing.Size(15, 14);
            this.chkEnable.TabIndex = 1;
            this.chkEnable.UseVisualStyleBackColor = true;
            this.chkEnable.CheckedChanged += new System.EventHandler(this.chkEnable_CheckedChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(20, 33);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(215, 12);
            this.label1.TabIndex = 0;
            this.label1.Text = "• Enable Lamp Life Data Collection";
            // 
            // ucLampLifePanel
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.groupBox1);
            this.Name = "ucLampLifePanel";
            this.Size = new System.Drawing.Size(676, 340);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.CheckBox chkEnable;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button btnManualCollect;
        private System.Windows.Forms.Label lblLastCollect;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label lblInfo;
    }
}
