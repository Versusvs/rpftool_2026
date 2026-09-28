using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RPFTool
{
    public class AddResourceForm : Form
    {
        private readonly List<byte> _knownTypes;
        private readonly byte _defaultType;
        private readonly uint _defaultFlags;
        private ComboBox cmbType;
        private TextBox txtCustomType;
        private TextBox txtFlags;
        private Label lblHint;
        private RadioButton rbList, rbCustom;
        private Button btnOk, btnCancel;

        public byte ChosenType { get; private set; }
        public uint ChosenFlags { get; private set; }

        public AddResourceForm(string fileName, string ext,
                               IEnumerable<byte> knownTypesForExt,
                               IEnumerable<byte> knownTypesGlobal,
                               byte defaultType, uint defaultFlags)
        {
            _defaultType = defaultType;
            _defaultFlags = defaultFlags;
            _knownTypes = new List<byte>();
            var seen = new HashSet<byte>();
            foreach (var t in knownTypesForExt) if (seen.Add(t)) _knownTypes.Add(t);
            foreach (var t in knownTypesGlobal) if (seen.Add(t)) _knownTypes.Add(t);
            BuildUi(fileName, ext);
        }

        private void BuildUi(string fileName, string ext)
        {
            this.Text = "Add RSC resource";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = this.MinimizeBox = false;
            // Раскладку считаем по фактическим размерам контролов,
            // поэтому автоскейлинг не нужен и только мешал бы
            this.AutoScaleMode = AutoScaleMode.None;
            this.ClientSize = new Size(460, 200);   // высота будет пересчитана в конце

            int y = 12;

            var lblFile = new Label();
            lblFile.Text = "File: " + fileName;
            lblFile.AutoSize = true;
            lblFile.Font = new Font(this.Font, FontStyle.Bold);
            lblFile.Location = new Point(12, y);
            y = lblFile.Bottom + 8;

            var lblExt = new Label();
            lblExt.Text = "Extension: " + (string.IsNullOrEmpty(ext) ? "(none)" : ext);
            lblExt.AutoSize = true;
            lblExt.Location = new Point(12, y);
            y = lblExt.Bottom + 10;

            var lblDesc = new Label();
            lblDesc.Text =
                "\"Version\" in the Attributes column is the ResourceType byte stored in the TOC entry.\r\n" +
                "Choose the value that matches the original archive entry for this file.";
            lblDesc.Location = new Point(12, y);
            lblDesc.Size = new Size(436, 40);
            y = lblDesc.Bottom + 10;

            rbList = new RadioButton();
            rbList.Text = "Choose from types seen in this archive:";
            rbList.AutoSize = true;
            rbList.Location = new Point(12, y);
            rbList.Checked = _knownTypes.Count > 0;
            rbList.Enabled = _knownTypes.Count > 0;
            int yList = rbList.Bottom + 4;

            cmbType = new ComboBox();
            cmbType.Location = new Point(30, yList);
            cmbType.Width = 418;
            cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbType.Enabled = rbList.Checked;
            foreach (var t in _knownTypes)
                cmbType.Items.Add(string.Format("{0} (0x{0:X2})", t));
            if (cmbType.Items.Count > 0)
            {
                int defIdx = _knownTypes.IndexOf(_defaultType);
                cmbType.SelectedIndex = (defIdx >= 0) ? defIdx : 0;
            }
            y = cmbType.Bottom + 10;

            rbCustom = new RadioButton();
            rbCustom.Text = "Enter custom value (decimal, 0..255):";
            rbCustom.AutoSize = true;
            rbCustom.Location = new Point(12, y);
            rbCustom.Checked = _knownTypes.Count == 0;
            int yCustom = rbCustom.Bottom + 4;

            txtCustomType = new TextBox();
            txtCustomType.Location = new Point(30, yCustom);
            txtCustomType.Width = 100;
            txtCustomType.Text = _defaultType.ToString();
            txtCustomType.Enabled = rbCustom.Checked;
            y = txtCustomType.Bottom + 10;

            var lblFlags = new Label();
            lblFlags.Text = "RSCFlags (hex, default C0000000):";
            lblFlags.AutoSize = true;
            lblFlags.Location = new Point(12, y + 3);

            txtFlags = new TextBox();
            txtFlags.Location = new Point(200, y);
            txtFlags.Width = 120;
            txtFlags.Text = _defaultFlags.ToString("X8");
            y = txtFlags.Bottom + 10;

            lblHint = new Label();
            lblHint.Location = new Point(12, y);
            lblHint.Size = new Size(436, 20);
            lblHint.ForeColor = Color.DarkRed;
            y = lblHint.Bottom + 8;

            // Кнопки ставятся ПОСЛЕ всего контента, а не по жёстким координатам
            btnOk = new Button();
            btnOk.Text = "Add";
            btnOk.Size = new Size(85, 28);
            btnOk.Location = new Point(this.ClientSize.Width - 2 * 85 - 24, y);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(this.ClientSize.Width - 85 - 12, y);
            y = btnOk.Bottom + 12;

            // Подписки — когда все контролы уже созданы
            rbList.CheckedChanged += (s, e) =>
            {
                cmbType.Enabled = rbList.Checked;
                txtCustomType.Enabled = !rbList.Checked;
            };
            rbCustom.CheckedChanged += (s, e) =>
            {
                cmbType.Enabled = !rbCustom.Checked;
                txtCustomType.Enabled = rbCustom.Checked;
            };
            btnOk.Click += BtnOk_Click;

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            this.Controls.AddRange(new Control[] {
                lblFile, lblExt, lblDesc,
                rbList, cmbType, rbCustom, txtCustomType,
                lblFlags, txtFlags, lblHint, btnOk, btnCancel
            });

            // Высота формы ровно по содержимому — ничего не обрежется
            this.ClientSize = new Size(460, y);
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            byte t;
            if (rbList.Checked)
            {
                if (cmbType.SelectedIndex < 0) { lblHint.Text = "Select a type."; return; }
                t = _knownTypes[cmbType.SelectedIndex];
            }
            else
            {
                if (!byte.TryParse(txtCustomType.Text.Trim(), out t))
                { lblHint.Text = "Custom value must be 0..255."; return; }
            }

            uint f;
            string fs = txtFlags.Text.Trim();
            if (fs.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) fs = fs.Substring(2);
            if (!uint.TryParse(fs, System.Globalization.NumberStyles.HexNumber, null, out f))
            { lblHint.Text = "Flags must be hex."; return; }

            if ((f & 0xC0000000u) != 0xC0000000u)
            {
                var r = MessageBox.Show(
                    "RSCFlags do not have the 0xC0000000 resource marker bits. Continue?",
                    "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            ChosenType = t;
            ChosenFlags = f;
            this.DialogResult = DialogResult.OK;
        }
    }
}