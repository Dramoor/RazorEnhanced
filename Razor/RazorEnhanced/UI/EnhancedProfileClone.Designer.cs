namespace RazorEnhanced.UI
{
    partial class EnhancedProfileClone
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(EnhancedProfileClone));
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.profilename = new System.Windows.Forms.TextBox();
            this.close = new System.Windows.Forms.Button();
            this.profileadd = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.cloneNameLabel = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // profilename
            // 
            this.profilename.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.profilename.BackColor = System.Drawing.Color.White;
            this.profilename.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.profilename.Location = new System.Drawing.Point(88, 33);
            this.profilename.Name = "profilename";
            this.profilename.Size = new System.Drawing.Size(210, 20);
            this.profilename.TabIndex = 0;
            // 
            // close
            // 
            this.close.Location = new System.Drawing.Point(39, 69);
            this.close.Name = "close";
            this.close.Size = new System.Drawing.Size(75, 25);
            this.close.TabIndex = 2;
            this.close.Text = "Close";
            this.close.UseVisualStyleBackColor = true;
            this.close.Click += new System.EventHandler(this.close_Click);
            // 
            // profileadd
            // 
            this.profileadd.Location = new System.Drawing.Point(196, 69);
            this.profileadd.Name = "profileadd";
            this.profileadd.Size = new System.Drawing.Size(75, 25);
            this.profileadd.TabIndex = 3;
            this.profileadd.Text = "Clone";
            this.profileadd.UseVisualStyleBackColor = true;
            this.profileadd.Click += new System.EventHandler(this.profileadd_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 36);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(63, 14);
            this.label1.TabIndex = 5;
            this.label1.Text = "New Name:";
            // 
            // cloneNameLabel
            // 
            this.cloneNameLabel.AutoSize = true;
            this.cloneNameLabel.Location = new System.Drawing.Point(12, 10);
            this.cloneNameLabel.Name = "cloneNameLabel";
            this.cloneNameLabel.Size = new System.Drawing.Size(101, 14);
            this.cloneNameLabel.TabIndex = 6;
            this.cloneNameLabel.Text = "Profile to Clone: null";
            // 
            // EnhancedProfileClone
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 14F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(310, 104);
            this.Controls.Add(this.cloneNameLabel);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.profileadd);
            this.Controls.Add(this.close);
            this.Controls.Add(this.profilename);
            this.Font = new System.Drawing.Font("Arial", 8F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "EnhancedProfileClone";
            this.Text = "Enhanced Profile Clone";
            this.Load += new System.EventHandler(this.EnhancedProfileAdd_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.TextBox profilename;
        private System.Windows.Forms.Button close;
        private System.Windows.Forms.Button profileadd;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label cloneNameLabel;

    }
}