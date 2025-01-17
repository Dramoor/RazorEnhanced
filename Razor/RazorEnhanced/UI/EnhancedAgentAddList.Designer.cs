namespace RazorEnhanced.UI
{
    partial class EnhancedAgentAddList
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(EnhancedAgentAddList));
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.AgentListToAdd = new System.Windows.Forms.TextBox();
            this.agentcloseItemList = new System.Windows.Forms.Button();
            this.agentaddItemList = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // AgentListToAdd
            // 
            this.AgentListToAdd.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.AgentListToAdd.BackColor = System.Drawing.Color.White;
            this.AgentListToAdd.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.AgentListToAdd.Location = new System.Drawing.Point(12, 13);
            this.AgentListToAdd.Name = "AgentListToAdd";
            this.AgentListToAdd.Size = new System.Drawing.Size(286, 20);
            this.AgentListToAdd.TabIndex = 0;
            // 
            // agentcloseItemList
            // 
            this.agentcloseItemList.Location = new System.Drawing.Point(39, 44);
            this.agentcloseItemList.Name = "agentcloseItemList";
            this.agentcloseItemList.Size = new System.Drawing.Size(75, 25);
            this.agentcloseItemList.TabIndex = 2;
            this.agentcloseItemList.Text = "Close";
            this.agentcloseItemList.UseVisualStyleBackColor = true;
            this.agentcloseItemList.Click += new System.EventHandler(this.EnhancedAgentCloseItemList_Click);
            // 
            // agentaddItemList
            // 
            this.agentaddItemList.Location = new System.Drawing.Point(196, 44);
            this.agentaddItemList.Name = "agentaddItemList";
            this.agentaddItemList.Size = new System.Drawing.Size(75, 25);
            this.agentaddItemList.TabIndex = 3;
            this.agentaddItemList.Text = "Add";
            this.agentaddItemList.UseVisualStyleBackColor = true;
            this.agentaddItemList.Click += new System.EventHandler(this.EnhancedAgentAddList_Click);
            // 
            // EnhancedAgentAddList
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 14F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(310, 80);
            this.Controls.Add(this.agentaddItemList);
            this.Controls.Add(this.agentcloseItemList);
            this.Controls.Add(this.AgentListToAdd);
            this.Font = new System.Drawing.Font("Arial", 8F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "EnhancedAgentAddList";
            this.Text = "Agent add List";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.TextBox AgentListToAdd;
        private System.Windows.Forms.Button agentcloseItemList;
        private System.Windows.Forms.Button agentaddItemList;

    }
}