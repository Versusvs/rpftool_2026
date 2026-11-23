using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using RPFLib.Resources;

namespace RPFTool
{
    public class PackNewResourceForm : Form
    {
        private readonly byte[] _flat;
        private TextBox txtFlat, txtVersion, txtSizeV, txtSizeP;
        private ComboBox cmbContainer;
        private CheckBox chkAuto;
        private Label lblFlatInfo, lblStatus, lblEncoding;
        private Button btnOk, btnCancel;
        private CheckBox chkEncrypt;

        public PackNewResourceForm(byte[] flat, string path)
        {
            _flat = flat;
            BuildUi();
            txtFlat.Text = path;
            cmbContainer.SelectedIndex = 0;
            txtVersion.Text = "63";
            chkAuto.Checked = true;   // сразу запускает авто-детект и пересчёт
        }

        public RSCFile.PackNewOptions Options
        {
            get
            {
                RSCFile.PackNewOptions o = new RSCFile.PackNewOptions();
                o.UseZlib = (cmbContainer.SelectedIndex == 0);
                o.Version = ParseDec(txtVersion.Text);
                o.SizeV = ParseHex(txtSizeV.Text);
                o.SizeP = ParseHex(txtSizeP.Text);
                o.Encrypt = chkEncrypt.Checked;
                return o;
            }
        }

        private void BuildUi()
        {
            this.Text = "Pack New Resource (no donor)";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(500, 320);

            int y = 12;
            Label lblFlat = new Label();
            lblFlat.Text = "Unpacked (flat) file:";
            lblFlat.Location = new Point(12, y + 3);
            lblFlat.AutoSize = true;

            txtFlat = new TextBox();
            txtFlat.Location = new Point(120, y);
            txtFlat.Width = 368;
            txtFlat.ReadOnly = true;

            lblFlatInfo = new Label();
            lblFlatInfo.Location = new Point(120, y + 24);
            lblFlatInfo.AutoSize = true;
            y += 50;

            Label lblCont = new Label();
            lblCont.Text = "Container:";
            lblCont.Location = new Point(12, y + 3);
            lblCont.AutoSize = true;

            cmbContainer = new ComboBox();
            cmbContainer.Location = new Point(120, y);
            cmbContainer.Width = 368;
            cmbContainer.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbContainer.Items.Add("packed_v2 - zlib (magic 0x06435352)");
            cmbContainer.Items.Add("packed_v1 - LZX (magic 0x05435352)");
            cmbContainer.SelectedIndexChanged += delegate {
                // zlib не шифруется никогда: сбрасываем флаг при переходе на zlib
                if (cmbContainer.SelectedIndex == 0 && chkEncrypt.Checked)
                    chkEncrypt.Checked = false;
                  Recalc(); 
                   };
            y += 30;

            Label lblVer = new Label();
            lblVer.Text = "Version (dec):";
            lblVer.Location = new Point(12, y + 3);
            lblVer.AutoSize = true;

            txtVersion = new TextBox();
            txtVersion.Location = new Point(120, y);
            txtVersion.Width = 80;
            txtVersion.TextChanged += delegate { Recalc(); };

            Label lblVerHint = new Label();
            lblVerHint.Text = "hint: xrsc=63, xspm=19, xct=50, xtl/xtp=83";
            lblVerHint.Location = new Point(210, y + 3);
            lblVerHint.AutoSize = true;
            y += 30;

            chkAuto = new CheckBox();
            chkAuto.Text = "Auto-detect SizeV/SizeP from RAGE pointers (0x50.../0x60...)";
            chkAuto.Location = new Point(12, y);
            chkAuto.AutoSize = true;
            chkAuto.CheckedChanged += delegate
            {
                txtSizeV.ReadOnly = chkAuto.Checked;
                txtSizeP.ReadOnly = chkAuto.Checked;
                if (chkAuto.Checked) AutoDetect();
                Recalc();
            };
            y += 26;
            chkEncrypt = new CheckBox();
            chkEncrypt.Text = "Encrypt (AES): контейнер 0x85435352, version 2, only LZX";
            chkEncrypt.Location = new Point(12, y);
            chkEncrypt.AutoSize = true;
            chkEncrypt.CheckedChanged += delegate
            {
                if (chkEncrypt.Checked)
                {
                    cmbContainer.SelectedIndex = 1;  // принудительно LZX
                    txtVersion.Text = "2";           // version обязан быть 2 для AES
                    txtVersion.ReadOnly = true;
                }
                else
                {
                    txtVersion.ReadOnly = false;
                }
                Recalc();
            };
            this.Controls.Add(chkEncrypt);
            y += 26;

            Label lblV = new Label();
            lblV.Text = "SizeV (hex):";
            lblV.Location = new Point(12, y + 3);
            lblV.AutoSize = true;
            txtSizeV = new TextBox();
            txtSizeV.Location = new Point(120, y);
            txtSizeV.Width = 100;
            txtSizeV.TextChanged += delegate { Recalc(); };

            Label lblP = new Label();
            lblP.Text = "SizeP (hex):";
            lblP.Location = new Point(230, y + 3);
            lblP.AutoSize = true;
            txtSizeP = new TextBox();
            txtSizeP.Location = new Point(300, y);
            txtSizeP.Width = 100;
            txtSizeP.TextChanged += delegate { Recalc(); };

            Button btnDetect = new Button();
            btnDetect.Text = "Detect";
            btnDetect.Location = new Point(410, y - 2);
            btnDetect.Width = 78;
            btnDetect.Click += delegate { AutoDetect(); Recalc(); };
            y += 32;

            lblStatus = new Label();
            lblStatus.Location = new Point(12, y);
            lblStatus.MaximumSize = new Size(476, 0);
            y += 40;

            lblEncoding = new Label();
            lblEncoding.Location = new Point(12, y);
            lblEncoding.AutoSize = true;                 // растягиваться под текст
            lblEncoding.MaximumSize = new Size(476, 0); // но не шире формы, с переносом строк
            lblEncoding.Text = "";
            this.Controls.Add(lblEncoding);             // <-- положить лейбл на форму
            y += 40;

            btnOk = new Button();
            btnOk.Text = "Pack";
            btnOk.Location = new Point(310, y);
            btnOk.Size = new Size(85, 28);
            btnOk.Click += delegate
            {
                if (btnOk.Enabled) this.DialogResult = DialogResult.OK;
            };

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(403, y);
            btnCancel.Size = new Size(85, 28);
            btnCancel.DialogResult = DialogResult.Cancel;

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            this.Controls.AddRange(new Control[] {
                lblFlat, txtFlat, lblFlatInfo,
                lblCont, cmbContainer,
                lblVer, txtVersion, lblVerHint,
                chkAuto,
                lblV, txtSizeV, lblP, txtSizeP, btnDetect,
                lblStatus, lblEncoding,
                btnOk, btnCancel });
        }

        private void AutoDetect()
        {
            int sv;
            int sp;

            if (RSCFile.ScanSizes(_flat, out sv, out sp))
            {
                txtSizeV.Text = sv.ToString("X");
                txtSizeP.Text = sp.ToString("X");
            }
            else
            {
                txtSizeV.Text = "";
                txtSizeP.Text = "";

                if (lblStatus != null)
                {
                    lblStatus.Text = "Can't detect V/P: no pointers 0x50xxxxxx / 0x60xxxxxx.";
                    lblStatus.ForeColor = Color.DarkRed;
                }
            }

            Recalc();
        }

        private void Recalc()
        {
            int v = ParseHex(txtSizeV.Text);
            int p = ParseHex(txtSizeP.Text);
            int ver = ParseDec(txtVersion.Text);
            int total = v + p;

            lblFlatInfo.Text = string.Format("length = {0} (0x{0:X})", _flat.Length);

            bool sizeOk = (v >= 0 && p >= 0 && total == _flat.Length);
            int flags1;
            bool encOk = RSCFile.TryBuildFlags1(v, p, out flags1);

            if (!sizeOk)
            {
                lblStatus.Text = string.Format(
                    "SizeV+SizeP = {0} (0x{0:X}) != flat length {1} (0x{1:X})", total, _flat.Length);
                lblStatus.ForeColor = Color.DarkRed;
            }
            else if (!encOk)
            {
                lblStatus.Text = "Sizes sum OK, but not encodable in flags1 (need multiples of 256).";
                lblStatus.ForeColor = Color.DarkRed;
            }
            else
            {
                lblStatus.Text = string.Format("OK: V=0x{0:X}, P=0x{1:X}, flags1=0x{2:X8}", v, p, flags1);
                lblStatus.ForeColor = Color.DarkGreen;
            }

            string encTxt = (cmbContainer.SelectedIndex == 0)
                ? "Payload: zlib level 9 (zlib1.dll), header = 12 bytes."
                : "Payload: LZX (xcompress32.dll), header = 12 bytes + 8 bytes preamble.";
             if (chkEncrypt.Checked)
                 encTxt = "Payload: LZX + AES: header 16 bytes (0x85435352, version 2, flags2=0), body pad to 16 and encrypts.";
             lblEncoding.Text = encTxt;
            btnOk.Enabled = sizeOk && encOk && (ver >= 0);
        }

        private static int ParseHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            int val;
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val))
                return val;
            return -1;
        }

        private static int ParseDec(string s)
        {
            int val;
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                return val;
            return -1;
        }
    }
}