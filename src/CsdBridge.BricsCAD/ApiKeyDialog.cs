using System.Drawing;
using System.Windows.Forms;

namespace CsdBridge.BricsCAD
{
    /// <summary>Masked input for the Anthropic API key, so it never appears on the command line.</summary>
    internal sealed class ApiKeyDialog : Form
    {
        private readonly TextBox _key;

        public ApiKeyDialog()
        {
            Text = "CSD-Bridge - Anthropic API key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 150);
            Font = new Font("Segoe UI", 9f);

            var label = new Label
            {
                Text = "Paste your Anthropic API key. It is stored encrypted for your Windows account.\n" +
                       "Leave blank and press OK to remove a stored key.",
                Location = new Point(12, 12),
                Size = new Size(436, 40),
            };

            _key = new TextBox { Location = new Point(12, 60), Width = 436, UseSystemPasswordChar = true };

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(292, 108), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(373, 108), Width = 75 };

            Controls.AddRange(new Control[] { label, _key, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string ApiKey => _key.Text.Trim();
    }
}
