using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RPFTool
{
    // ЕДИНОЕ окно добавления файла. Состав секций всегда одинаковый;
    // неприменимые к типу файла секции БЛОКИРУЮТСЯ (Enabled = false):
    //   RSC-ресурс   -> тип и RSCFlags активны, режим хранения зафиксирован на None (сырьё);
    //   обычный файл -> тип и RSCFlags заблокированы, режим хранения выбирается.
    // Поддерживаются обе сигнатуры вызова:
    //   6 аргументов (старые вызовы Version3, только для RSC) = isResource:true, defaultCompressed:false;
    //   8 аргументов (унифицированные вызовы) - тип файла и дефолтный режим задаются явно.
    public class AddResourceForm : Form
    {
        private readonly List<byte> _knownTypes;      // объединённый список типов (ext + глобально)
        private readonly int _countForExt;            // сколько типов пришло именно для этого расширения
        private readonly byte _defaultType;
        private readonly uint _defaultFlags;
        private readonly bool _isResource;
        private readonly bool _defaultCompressed;

        private ComboBox cmbType;
        private TextBox txtCustomType;
        private TextBox txtFlags;
        private Label lblHint;
        private Label lblTypesInfo;
        private Label lblTypeNote;
        private Label lblModeNote;
        private RadioButton rbList, rbCustom;
        private RadioButton rbCompressed, rbNone;
        private CheckBox chkApplyAll;
        private Button btnOk, btnCancel;

        public byte ChosenType { get; private set; }
        public uint ChosenFlags { get; private set; }
        public bool ChosenCompressed { get; private set; }
        public bool ApplyToAll { get; private set; }

        // Старая сигнатура: форма открывалась только для RSC-ресурсов
        public AddResourceForm(string fileName, string ext,
                               IEnumerable<byte> knownTypesForExt,
                               IEnumerable<byte> knownTypesGlobal,
                               byte defaultType, uint defaultFlags)
            : this(fileName, ext, knownTypesForExt, knownTypesGlobal,
                   defaultType, defaultFlags, true, false)
        { }

        public AddResourceForm(string fileName, string ext,
                               IEnumerable<byte> knownTypesForExt,
                               IEnumerable<byte> knownTypesGlobal,
                               byte defaultType, uint defaultFlags,
                               bool isResource, bool defaultCompressed)
        {
            _defaultType = defaultType;
            _defaultFlags = defaultFlags;
            _isResource = isResource;
            _defaultCompressed = defaultCompressed;
            _knownTypes = new List<byte>();
            var seen = new HashSet<byte>();
            if (knownTypesForExt != null)
                foreach (var t in knownTypesForExt)
                    if (seen.Add(t)) _knownTypes.Add(t);
            _countForExt = _knownTypes.Count;
            if (knownTypesGlobal != null)
                foreach (var t in knownTypesGlobal)
                    if (seen.Add(t)) _knownTypes.Add(t);
            _knownTypes.Sort();
            BuildUi(fileName, ext);
        }

        private void BuildUi(string fileName, string ext)
        {
            this.Text = _isResource ? "Add RSC resource" : "Add file";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = this.MinimizeBox = false;
            this.AutoScaleMode = AutoScaleMode.None;
            this.ClientSize = new Size(460, 200);   // высота пересчитывается в конце

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

            // ДИАГНОСТИКА: сразу видно, сколько типов реально пришло из открытого архива
            lblTypesInfo = new Label();
            lblTypesInfo.AutoSize = true;
            lblTypesInfo.Location = new Point(12, y);
            lblTypesInfo.ForeColor = Color.DarkSlateBlue;
            if (_knownTypes.Count > 0)
                lblTypesInfo.Text = string.Format(
                    "Resource types found in this archive: {0} total, {1} for extension '{2}'.",
                    _knownTypes.Count, _countForExt, ext);
            else
                lblTypesInfo.Text =
                    "No resource types found in this archive: the list is empty.\r\n" +
                    "Enter the value manually below (it is stored in the TOC entry).";
            y = lblTypesInfo.Bottom + 10;

            // --- Секция 1: тип ресурса + флаги (блокируется для обычных файлов) ---
            var lblSecType = new Label();
            lblSecType.Text = "Resource type (\"Version\" in the Attributes column):";
            lblSecType.AutoSize = true;
            lblSecType.Location = new Point(12, y);
            y = lblSecType.Bottom + 4;

            rbList = new RadioButton();
            rbList.Text = "Choose from types seen in this archive:";
            rbList.AutoSize = true;
            rbList.Location = new Point(12, y);
            int yCombo = rbList.Bottom + 4;

            cmbType = new ComboBox();
            cmbType.Location = new Point(30, yCombo);
            cmbType.Width = 418;
            cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (var t in _knownTypes)
                cmbType.Items.Add(string.Format("{0} (0x{0:X2})", t));
            if (cmbType.Items.Count > 0)
            {
                int defIdx = _knownTypes.IndexOf(_defaultType);
                cmbType.SelectedIndex = (defIdx >= 0) ? defIdx : 0;
            }
            y = cmbType.Bottom + 6;

            rbCustom = new RadioButton();
            rbCustom.Text = "Enter custom value (decimal, 0..255):";
            rbCustom.AutoSize = true;
            rbCustom.Location = new Point(12, y);
            int yCustomBox = rbCustom.Bottom + 4;

            txtCustomType = new TextBox();
            txtCustomType.Location = new Point(30, yCustomBox);
            txtCustomType.Width = 100;
            txtCustomType.Text = _defaultType.ToString();
            y = txtCustomType.Bottom + 6;

            var lblFlags = new Label();
            lblFlags.Text = "RSCFlags (hex):";
            lblFlags.AutoSize = true;
            lblFlags.Location = new Point(30, y + 3);

            txtFlags = new TextBox();
            txtFlags.Location = new Point(200, y);
            txtFlags.Width = 120;
            txtFlags.Text = _defaultFlags.ToString("X8");
            y = txtFlags.Bottom + 8;

            lblTypeNote = new Label();
            lblTypeNote.Text = "Not an RSC resource: the fields above do not apply and are blocked.";
            lblTypeNote.AutoSize = true;
            lblTypeNote.Location = new Point(30, y);
            lblTypeNote.ForeColor = Color.Gray;
            lblTypeNote.Visible = !_isResource;
            if (!_isResource) y = lblTypeNote.Bottom + 10; else y += 2;

            // --- Секция 2: режим хранения (блокируется для ресурсов) ---
            var lblSecMode = new Label();
            lblSecMode.Text = "Storage mode:";
            lblSecMode.AutoSize = true;
            lblSecMode.Location = new Point(12, y);
            y = lblSecMode.Bottom + 4;

            rbCompressed = new RadioButton();
            rbCompressed.Text = "Compressed (deflate, like native MC:LA files)";
            rbCompressed.AutoSize = true;
            rbCompressed.Location = new Point(30, y);
            y = rbCompressed.Bottom + 2;

            rbNone = new RadioButton();
            rbNone.Text = "None (store raw, uncompressed)";
            rbNone.AutoSize = true;
            rbNone.Location = new Point(30, y);
            y = rbNone.Bottom + 6;

            chkApplyAll = new CheckBox();
            chkApplyAll.Text = "Apply this storage mode to all files in this add operation";
            chkApplyAll.AutoSize = true;
            chkApplyAll.Location = new Point(30, y);
            y = chkApplyAll.Bottom + 6;

            lblModeNote = new Label();
            lblModeNote.Text = "RSC resources are always stored raw: the TOC has no compression bit for resource entries.";
            lblModeNote.AutoSize = true;
            lblModeNote.Location = new Point(30, y);
            lblModeNote.ForeColor = Color.Gray;
            lblModeNote.Visible = _isResource;
            if (_isResource) y = lblModeNote.Bottom + 8; else y += 2;

            lblHint = new Label();
            lblHint.Location = new Point(12, y);
            lblHint.Size = new Size(436, 20);
            lblHint.ForeColor = Color.DarkRed;
            y = lblHint.Bottom + 8;

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

            // Подписки ТОЛЬКО после того, как все контролы созданы
            rbList.CheckedChanged += (s, e) =>
            {
                if (!_isResource) return;
                cmbType.Enabled = rbList.Checked;
                txtCustomType.Enabled = !rbList.Checked;
            };
            rbCustom.CheckedChanged += (s, e) =>
            {
                if (!_isResource) return;
                cmbType.Enabled = !rbCustom.Checked;
                txtCustomType.Enabled = rbCustom.Checked;
            };
            btnOk.Click += BtnOk_Click;

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            this.Controls.AddRange(new Control[] {
                lblFile, lblExt, lblTypesInfo,
                lblSecType, rbList, cmbType, rbCustom, txtCustomType, lblFlags, txtFlags, lblTypeNote,
                lblSecMode, rbCompressed, rbNone, chkApplyAll, lblModeNote,
                lblHint, btnOk, btnCancel
            });

            // ИСПРАВЛЕНО: состояния утверждаются ПОСЛЕ добавления в Controls.
            // Checked/Enabled, выставленные до parent-инга, могут сбрасываться при
            // создании handle (в т.ч. под скинами DevExpress) - из-за этого секции
            // оставались серыми/неактивными даже когда данные для них есть.
            if (_isResource)
            {
                if (_knownTypes.Count > 0) { rbList.Checked = true; rbCustom.Checked = false; }
                else { rbList.Checked = false; rbCustom.Checked = true; }
                rbList.Enabled = _knownTypes.Count > 0;
                cmbType.Enabled = rbList.Checked;
                txtCustomType.Enabled = rbCustom.Checked;

                rbCompressed.Checked = false;
                rbNone.Checked = true;
                rbCompressed.Enabled = false;
                rbNone.Enabled = false;
                chkApplyAll.Checked = false;
                chkApplyAll.Enabled = false;
            }
            else
            {
                rbList.Checked = false;
                rbCustom.Checked = false;
                rbList.Enabled = false;
                cmbType.Enabled = false;
                txtCustomType.Enabled = false;
                lblFlags.Enabled = false;
                txtFlags.Enabled = false;

                rbCompressed.Checked = _defaultCompressed;
                rbNone.Checked = !_defaultCompressed;
                rbCompressed.Enabled = true;
                rbNone.Enabled = true;
                chkApplyAll.Enabled = true;
            }

            // Высота строго по содержимому - ничего не обрежется на любом DPI
            this.ClientSize = new Size(460, y);
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (_isResource)
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
                ChosenCompressed = false;   // ресурсы всегда сырыми
                ApplyToAll = false;
            }
            else
            {
                ChosenCompressed = rbCompressed.Checked;
                ApplyToAll = chkApplyAll.Checked;
                ChosenType = _defaultType;      // для обычных файлов не используется
                ChosenFlags = _defaultFlags;    // для обычных файлов не используется
            }

            this.DialogResult = DialogResult.OK;
        }
    }
}