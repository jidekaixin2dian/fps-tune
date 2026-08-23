[CmdletBinding()]
param(
    [switch]$SmokeTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ---------------------------------------------------------------------------
# 自定义控件：圆角卡片与按钮（纯视觉，不影响功能逻辑）
# ---------------------------------------------------------------------------
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeltaForceTuneGui
{
    public static class Draw
    {
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int r = radius;
            if (r < 1) r = 1;
            if (r > bounds.Width / 2) r = Math.Max(1, bounds.Width / 2);
            if (r > bounds.Height / 2) r = Math.Max(1, bounds.Height / 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, r * 2, r * 2, 180, 90);
            path.AddArc(bounds.Right - r * 2, bounds.Y, r * 2, r * 2, 270, 90);
            path.AddArc(bounds.Right - r * 2, bounds.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class RoundedButton : Button
    {
        public int CornerRadius { get; set; }
        public Color FillColor { get; set; }
        public Color HoverFillColor { get; set; }
        public Color PressedFillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color HoverBorderColor { get; set; }
        public bool IsPrimary { get; set; }
        public Color GradientStart { get; set; }
        public Color GradientEnd { get; set; }

        private bool _hover;
        private bool _pressed;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            CornerRadius = 8;
            FillColor = Color.FromArgb(24, 24, 27);
            HoverFillColor = Color.FromArgb(32, 32, 36);
            PressedFillColor = Color.FromArgb(16, 16, 18);
            BorderColor = Color.FromArgb(50, 50, 54);
            HoverBorderColor = Color.FromArgb(90, 90, 96);
            GradientStart = Color.FromArgb(16, 185, 129);
            GradientEnd = Color.FromArgb(34, 211, 238);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Draw.RoundedRect(rect, CornerRadius))
            {
                if (!Enabled)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(45, 45, 49)))
                        g.FillPath(b, path);
                }
                else if (IsPrimary)
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(rect, GradientStart, GradientEnd, 35f))
                        g.FillPath(b, path);
                }
                else
                {
                    Color fill = _pressed ? PressedFillColor : (_hover ? HoverFillColor : FillColor);
                    using (SolidBrush b = new SolidBrush(fill))
                        g.FillPath(b, path);
                }

                Color border = !Enabled ? Color.FromArgb(50, 50, 54) : (_hover ? HoverBorderColor : BorderColor);
                using (Pen p = new Pen(border, 1))
                    g.DrawPath(p, path);
            }

            Color textColor = !Enabled ? Color.FromArgb(110, 110, 116) : ForeColor;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public bool DrawBorder { get; set; }

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            CornerRadius = 12;
            FillColor = Color.FromArgb(17, 17, 19);
            BorderColor = Color.FromArgb(38, 38, 42);
            DrawBorder = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Draw.RoundedRect(rect, CornerRadius))
            {
                using (SolidBrush b = new SolidBrush(FillColor))
                    g.FillPath(b, path);
                if (DrawBorder)
                {
                    using (Pen p = new Pen(BorderColor, 1))
                        g.DrawPath(p, path);
                }
            }
            base.OnPaint(e);
        }
    }
}
"@ -ReferencedAssemblies @('System.Drawing','System.Windows.Forms')

# ---------------------------------------------------------------------------
# 路径与全局状态
# ---------------------------------------------------------------------------
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Engine = Join-Path $Root 'delta-optimizer.ps1'
$Tuning = Join-Path $Root 'tuning-experiment.ps1'
$FriendTest = Join-Path $Root 'friend-test.ps1'
$TempDir = Join-Path $env:TEMP 'delta-gui-tmp'
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

$script:DetectData = $null
$script:ItemIdList = @()
$script:CurrentPage = 'detect'
$script:ThemeMode = 'dark'
$script:Theme = $null
$script:Pages = @{}
$script:NavButtons = @{}
$script:ThemeButtons = @{}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $enc = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

function To-PsLiteral {
    param([string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

function Invoke-GuiScript {
    param(
        [string]$Name,
        [string]$TargetScript,
        [string[]]$Arguments
    )
    $wrapper = Join-Path $TempDir "$Name.ps1"
    $stdout = Join-Path $TempDir "$Name.out.txt"
    $stderr = Join-Path $TempDir "$Name.err.txt"

    $line = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`n& " + (To-PsLiteral $TargetScript)
    foreach ($arg in $Arguments) {
        if ($arg -match '^[A-Za-z0-9_./:,+\-]+$') {
            $line += ' ' + $arg
        } else {
            $line += ' ' + (To-PsLiteral $arg)
        }
    }
    Write-Utf8File $wrapper $line

    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$wrapper) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $p.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
    $p.Refresh()
    $outText = ''
    $errText = ''
    if ((Test-Path $stdout) -and (Get-Item $stdout).Length -gt 0) { $outText = [System.IO.File]::ReadAllText($stdout) }
    if ((Test-Path $stderr) -and (Get-Item $stderr).Length -gt 0) { $errText = [System.IO.File]::ReadAllText($stderr) }
    return @{ exitCode = $p.ExitCode; output = $outText; error = $errText; stdout = $stdout; stderr = $stderr }
}

function Show-Text {
    param($TextBox, [string]$Text)
    $TextBox.Text = $Text
    $TextBox.SelectionStart = $TextBox.TextLength
    $TextBox.ScrollToCaret()
}

function Show-Result {
    param($TextBox, $Result, [string]$Title = '结果')
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("exit=$($Result.exitCode)")
    if ($Result.output) {
        [void]$sb.AppendLine('--- STDOUT ---')
        [void]$sb.AppendLine($Result.output)
    }
    if ($Result.error) {
        [void]$sb.AppendLine('--- STDERR ---')
        [void]$sb.AppendLine($Result.error)
    }
    Show-Text $TextBox $sb.ToString()
}

function ConvertFrom-JsonSafe {
    param([string]$Text)
    if (-not $Text) { return $null }
    try { return ($Text | ConvertFrom-Json) } catch { return $null }
}

# ---------------------------------------------------------------------------
# 主题系统
# ---------------------------------------------------------------------------
function Get-Color {
    param([string]$Hex)
    if ($Hex -match '^#([0-9A-Fa-f]{6})$') {
        return [System.Drawing.ColorTranslator]::FromHtml($Hex)
    }
    return [System.Drawing.Color]::Transparent
}

$script:Palettes = @{
    dark = @{
        Bg          = (Get-Color '#09090b')
        Sidebar     = (Get-Color '#0c0c0e')
        Surface     = (Get-Color '#111113')
        SurfaceAlt  = (Get-Color '#131316')
        Elevated    = (Get-Color '#18181b')
        InputBg     = (Get-Color '#0b0b0d')
        Border      = (Get-Color '#26262a')
        BorderHover = (Get-Color '#3f3f46')
        Text        = (Get-Color '#f4f4f5')
        Muted       = (Get-Color '#a1a1aa')
        Dim         = (Get-Color '#71717a')
        Primary     = (Get-Color '#10b981')
        Accent      = (Get-Color '#22d3ee')
        Danger      = (Get-Color '#f87171')
        Warning     = (Get-Color '#fbbf24')
        Ok          = (Get-Color '#34d399')
        Disabled    = (Get-Color '#52525b')
        NavActive   = (Get-Color '#18181b')
        NavHover    = (Get-Color '#1f1f23')
        Hover       = (Get-Color '#202024')
        Pressed     = (Get-Color '#0e0e10')
        GradientStart = (Get-Color '#10b981')
        GradientEnd   = (Get-Color '#22d3ee')
    }
    light = @{
        Bg          = (Get-Color '#f4f4f5')
        Sidebar     = (Get-Color '#fafafa')
        Surface     = (Get-Color '#ffffff')
        SurfaceAlt  = (Get-Color '#f4f4f5')
        Elevated    = (Get-Color '#ffffff')
        InputBg     = (Get-Color '#fafafa')
        Border      = (Get-Color '#e4e4e7')
        BorderHover = (Get-Color '#d4d4d8')
        Text        = (Get-Color '#18181b')
        Muted       = (Get-Color '#52525b')
        Dim         = (Get-Color '#71717a')
        Primary     = (Get-Color '#059669')
        Accent      = (Get-Color '#0891b2')
        Danger      = (Get-Color '#dc2626')
        Warning     = (Get-Color '#d97706')
        Ok          = (Get-Color '#059669')
        Disabled    = (Get-Color '#a1a1aa')
        NavActive   = (Get-Color '#e4e4e7')
        NavHover    = (Get-Color '#f4f4f5')
        Hover       = (Get-Color '#f4f4f5')
        Pressed     = (Get-Color '#e4e4e7')
        GradientStart = (Get-Color '#059669')
        GradientEnd   = (Get-Color '#0891b2')
    }
}

function Get-SystemTheme {
    try {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
        $val = (Get-ItemProperty -Path $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
        if ($val -eq 1) { return 'light' }
        return 'dark'
    } catch {
        return 'dark'
    }
}

function Update-ThemeControl {
    param($Control)
    if ($null -eq $Control) { return }
    $t = $script:Theme

    if ($Control -is [DeltaForceTuneGui.RoundedButton]) {
        $tag = [string]$Control.Tag
        if ($tag -eq 'primary') {
            $Control.IsPrimary = $true
            $Control.GradientStart = $t.GradientStart
            $Control.GradientEnd = $t.GradientEnd
            $Control.ForeColor = [System.Drawing.Color]::White
            $Control.FillColor = $t.Primary
            $Control.BorderColor = $t.Primary
        } elseif ($tag -eq 'danger') {
            $Control.IsPrimary = $false
            $Control.FillColor = [System.Drawing.Color]::FromArgb(40, 40, 40)
            $Control.HoverFillColor = [System.Drawing.Color]::FromArgb(55, 35, 35)
            $Control.PressedFillColor = [System.Drawing.Color]::FromArgb(30, 25, 25)
            $Control.BorderColor = $t.Danger
            $Control.HoverBorderColor = $t.Danger
            $Control.ForeColor = $t.Danger
        } else {
            $Control.IsPrimary = $false
            $Control.FillColor = $t.Elevated
            $Control.HoverFillColor = $t.Hover
            $Control.PressedFillColor = $t.Pressed
            $Control.BorderColor = $t.Border
            $Control.HoverBorderColor = $t.BorderHover
            $Control.ForeColor = $t.Text
        }
        $Control.Invalidate()
    }
    elseif ($Control -is [DeltaForceTuneGui.RoundedPanel]) {
        $tag = [string]$Control.Tag
        if ($tag -eq 'field') {
            $Control.FillColor = $t.InputBg
            $Control.BorderColor = $t.Border
        } elseif ($tag -eq 'banner') {
            $Control.FillColor = [System.Drawing.Color]::FromArgb(20, 30, 40)
            $Control.BorderColor = $t.Accent
        } else {
            $Control.FillColor = $t.Surface
            $Control.BorderColor = $t.Border
        }
        $Control.Invalidate()
    }
    elseif ($Control -is [System.Windows.Forms.Label]) {
        $tag = [string]$Control.Tag
        if ($tag -eq 'pageTitle') {
            $Control.ForeColor = $t.Text
            $Control.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 16, [System.Drawing.FontStyle]::Bold)
        } elseif ($tag -eq 'cardTitle') {
            $Control.ForeColor = $t.Muted
            $Control.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9, [System.Drawing.FontStyle]::Bold)
        } elseif ($tag -eq 'muted') {
            $Control.ForeColor = $t.Muted
        } elseif ($tag -eq 'dim') {
            $Control.ForeColor = $t.Dim
        } elseif ($tag -eq 'ok') {
            $Control.ForeColor = $t.Ok
        } elseif ($tag -eq 'attention') {
            $Control.ForeColor = $t.Warning
        } elseif ($tag -eq 'danger') {
            $Control.ForeColor = $t.Danger
        } else {
            $Control.ForeColor = $t.Text
        }
    }
    elseif ($Control -is [System.Windows.Forms.TextBox]) {
        $Control.BackColor = $t.InputBg
        $Control.ForeColor = $t.Text
        $Control.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    }
    elseif ($Control -is [System.Windows.Forms.CheckedListBox]) {
        $Control.BackColor = $t.InputBg
        $Control.ForeColor = $t.Text
        $Control.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    }
    elseif ($Control -is [System.Windows.Forms.CheckBox] -or $Control -is [System.Windows.Forms.RadioButton]) {
        $Control.BackColor = [System.Drawing.Color]::Transparent
        $Control.ForeColor = $t.Text
    }
    elseif ($Control -is [System.Windows.Forms.Panel]) {
        $tag = [string]$Control.Tag
        if ($tag -eq 'sidebar') {
            $Control.BackColor = $t.Sidebar
        } else {
            $Control.BackColor = $t.Bg
        }
    }
    elseif ($Control -is [System.Windows.Forms.TableLayoutPanel]) {
        $Control.BackColor = $t.Bg
    }

    if ($Control.HasChildren) {
        foreach ($child in $Control.Controls) {
            Update-ThemeControl $child
        }
    }
}

function Update-NavButtons {
    foreach ($key in $script:NavButtons.Keys) {
        $b = $script:NavButtons[$key]
        $t = $script:Theme
        $active = ($key -eq $script:CurrentPage)
        if ($active) {
            $b.FillColor = $t.NavActive
            $b.HoverFillColor = $t.NavActive
            $b.BorderColor = $t.Accent
            $b.HoverBorderColor = $t.Accent
            $b.ForeColor = $t.Text
        } else {
            $b.FillColor = [System.Drawing.Color]::Transparent
            $b.HoverFillColor = $t.NavHover
            $b.BorderColor = [System.Drawing.Color]::Transparent
            $b.HoverBorderColor = $t.BorderHover
            $b.ForeColor = $t.Muted
        }
        $b.Invalidate()
    }
}

function Update-ThemeButtons {
    foreach ($mode in $script:ThemeButtons.Keys) {
        $b = $script:ThemeButtons[$mode]
        $t = $script:Theme
        $active = ($mode -eq $script:ThemeMode)
        if ($active) {
            $b.FillColor = $t.NavActive
            $b.HoverFillColor = $t.NavActive
            $b.BorderColor = $t.Accent
            $b.HoverBorderColor = $t.Accent
            $b.ForeColor = $t.Text
        } else {
            $b.FillColor = [System.Drawing.Color]::Transparent
            $b.HoverFillColor = $t.NavHover
            $b.BorderColor = [System.Drawing.Color]::Transparent
            $b.HoverBorderColor = $t.BorderHover
            $b.ForeColor = $t.Muted
        }
        $b.Invalidate()
    }
}

function Invoke-ApplyTheme {
    param([string]$Mode = $script:ThemeMode)
    $resolved = $Mode
    if ($resolved -eq 'system') {
        $resolved = Get-SystemTheme
    }
    $script:ThemeMode = $Mode
    $script:Theme = $script:Palettes[$resolved]

    $form.BackColor = $script:Theme.Bg
    $sidebar.BackColor = $script:Theme.Sidebar
    $contentHost.BackColor = $script:Theme.Bg
    Update-ThemeControl $form
    Update-NavButtons
    Update-ThemeButtons
    $form.Invalidate()
}

# ---------------------------------------------------------------------------
# 控件创建辅助
# ---------------------------------------------------------------------------
function New-RoundedButton {
    param($Parent, [string]$Text, [int]$Width = 120, [int]$Height = 34, [string]$Tag = '')
    $b = New-Object DeltaForceTuneGui.RoundedButton
    $b.Text = $Text
    $b.Size = New-Object System.Drawing.Size($Width, $Height)
    $b.Tag = $Tag
    $b.AutoSize = $false
    $Parent.Controls.Add($b)
    return $b
}

function New-Card {
    param($Parent, [string]$Title = '')
    $card = New-Object DeltaForceTuneGui.RoundedPanel
    $card.Dock = 'Fill'
    $card.Margin = New-Object System.Windows.Forms.Padding(6)
    $card.Tag = 'card'
    $Parent.Controls.Add($card)
    if ($Title) {
        $lbl = New-Object System.Windows.Forms.Label
        $lbl.Text = $Title
        $lbl.Location = New-Object System.Drawing.Point(16, 12)
        $lbl.Size = New-Object System.Drawing.Size(200, 22)
        $lbl.Tag = 'cardTitle'
        $card.Controls.Add($lbl)
    }
    return $card
}

function Add-Label {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width = 200, [string]$Tag = '')
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $Text
    $l.Location = New-Object System.Drawing.Point($X, $Y)
    $l.Size = New-Object System.Drawing.Size($Width, 22)
    $l.Tag = $Tag
    $Parent.Controls.Add($l)
    return $l
}

function New-OutputBox {
    param($Parent, [int]$X, [int]$Y, [int]$Width, [int]$Height, $Anchor = 'Top,Bottom,Left,Right')
    $t = New-Object System.Windows.Forms.TextBox
    $t.Location = New-Object System.Drawing.Point($X, $Y)
    $t.Size = New-Object System.Drawing.Size($Width, $Height)
    $t.Multiline = $true
    $t.ScrollBars = 'Vertical'
    $t.BorderStyle = 'None'
    $t.Font = New-Object System.Drawing.Font('Consolas', 9)
    $t.Anchor = $Anchor
    $Parent.Controls.Add($t)
    return $t
}

function New-FieldTextBox {
    param($Parent, [int]$X, [int]$Y, [int]$Width, [int]$Height = 26)
    $field = New-Object DeltaForceTuneGui.RoundedPanel
    $field.Location = New-Object System.Drawing.Point($X, $Y)
    $field.Size = New-Object System.Drawing.Size($Width, $Height)
    $field.CornerRadius = 6
    $field.Tag = 'field'
    $Parent.Controls.Add($field)

    $tb = New-Object System.Windows.Forms.TextBox
    $tb.BorderStyle = 'None'
    $tb.Location = New-Object System.Drawing.Point(8, 4)
    $tb.Size = New-Object System.Drawing.Size($Width - 16, $Height - 8)
    $field.Controls.Add($tb)
    return $tb
}

function Set-ThemeMode {
    param([string]$Mode)
    $script:ThemeMode = $Mode
    Invoke-ApplyTheme $Mode
}

function Show-Page {
    param([string]$Key)
    $script:CurrentPage = $Key
    foreach ($entry in $script:Pages.Keys) {
        $script:Pages[$entry].Visible = ($entry -eq $Key)
    }
    Update-NavButtons
}

# ---------------------------------------------------------------------------
# 主窗体
# ---------------------------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'delta-force-tune · 系统层帧率优化'
$form.Size = New-Object System.Drawing.Size(1200, 780)
$form.MinimumSize = New-Object System.Drawing.Size(1180, 680)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
$root.ColumnCount = 2
$root.RowCount = 1
$root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Absolute, 220)))
$root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 100)))
$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))
$form.Controls.Add($root)

# 左侧导航栏
$sidebar = New-Object System.Windows.Forms.Panel
$sidebar.Dock = 'Fill'
$sidebar.Padding = New-Object System.Windows.Forms.Padding(12)
$sidebar.Tag = 'sidebar'
$root.Controls.Add($sidebar, 0, 0)

$logo = New-Object System.Windows.Forms.Label
$logo.Text = 'delta-force-tune'
$logo.Location = New-Object System.Drawing.Point(4, 10)
$logo.Size = New-Object System.Drawing.Size(190, 28)
$logo.Tag = 'pageTitle'
$logo.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 13, [System.Drawing.FontStyle]::Bold)
$sidebar.Controls.Add($logo)

$logoSub = New-Object System.Windows.Forms.Label
$logoSub.Text = '系统层帧率优化工具'
$logoSub.Location = New-Object System.Drawing.Point(4, 40)
$logoSub.Size = New-Object System.Drawing.Size(190, 20)
$logoSub.Tag = 'muted'
$sidebar.Controls.Add($logoSub)

$navDefs = @(
    @{ Key = 'detect'; Text = '检测' },
    @{ Key = 'opt'; Text = '优化' },
    @{ Key = 'ab'; Text = 'A/B 实验' },
    @{ Key = 'friend'; Text = '朋友测试' },
    @{ Key = 'backup'; Text = '备份 / 日志' }
)
$navY = 86
foreach ($def in $navDefs) {
    $btn = New-RoundedButton $sidebar $def.Text 196 44 'nav'
    $btn.Location = New-Object System.Drawing.Point(4, $navY)
    $btn.Tag = $def.Key
    $nk = $def.Key
    $btn.Add_Click({ Show-Page ([string]$this.Tag) })
    $script:NavButtons[$nk] = $btn
    $navY += 54
}

$themeLabel = New-Object System.Windows.Forms.Label
$themeLabel.Text = '主题'
$themeLabel.Location = New-Object System.Drawing.Point(4, 380)
$themeLabel.Size = New-Object System.Drawing.Size(190, 20)
$themeLabel.Tag = 'muted'
$sidebar.Controls.Add($themeLabel)

$themeDefs = @(
    @{ Key = 'dark'; Text = '深色' },
    @{ Key = 'light'; Text = '亮色' },
    @{ Key = 'system'; Text = '跟随系统' }
)
$themeY = 406
foreach ($def in $themeDefs) {
    $btn = New-RoundedButton $sidebar $def.Text 196 32 ''
    $btn.Location = New-Object System.Drawing.Point(4, $themeY)
    $btn.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 8.5)
    $mk = $def.Key
    $btn.Tag = $mk
    $btn.Add_Click({ Set-ThemeMode ([string]$this.Tag) })
    $script:ThemeButtons[$mk] = $btn
    $themeY += 40
}

# 右侧内容容器
$contentHost = New-Object System.Windows.Forms.Panel
$contentHost.Dock = 'Fill'
$contentHost.Padding = New-Object System.Windows.Forms.Padding(16)
$contentHost.Tag = 'content'
$root.Controls.Add($contentHost, 1, 0)

$uiWidth = 940

# =========================================================
# Tab 1: 检测
# =========================================================
$pageDetect = New-Object System.Windows.Forms.Panel
$pageDetect.Dock = 'Fill'
$contentHost.Controls.Add($pageDetect)
$script:Pages['detect'] = $pageDetect

$lblDetectTitle = Add-Label $pageDetect '检测' 4 4 200 32 'pageTitle'
$btnDetectRun = New-RoundedButton $pageDetect '运行检测' 120 36 'primary'
$btnDetectRun.Location = New-Object System.Drawing.Point($uiWidth - 280, 8)
$btnDetectRun.Anchor = 'Top,Right'
$pageDetect.Controls.Add($btnDetectRun)
$btnDetectLoad = New-RoundedButton $pageDetect '加载到优化页' 140 36 ''
$btnDetectLoad.Location = New-Object System.Drawing.Point($uiWidth - 150, 8)
$btnDetectLoad.Anchor = 'Top,Right'
$pageDetect.Controls.Add($btnDetectLoad)

$cardHw = New-Card $pageDetect '硬件信息'
$cardHw.Dock = 'None'
$cardHw.Size = New-Object System.Drawing.Size(440, 220)
$cardHw.Location = New-Object System.Drawing.Point(4, 52)
$cardHw.Anchor = 'Top,Left'
$hwRows = @(
    @('CPU', 'CPU'),
    @('GPU', 'GPU'),
    @('内存', '内存'),
    @('系统版本', '系统'),
    @('笔记本', '笔记本'),
    @('管理员', '管理员')
)
$hwY = 44
foreach ($row in $hwRows) {
    Add-Label $cardHw $row[0] 16 $hwY 90 'muted' | Out-Null
    Add-Label $cardHw ('--') 110 $hwY 280 '' | Out-Null
    $hwY += 28
}
$lblDetectCpu = $cardHw.Controls[2]
$lblDetectGpu = $cardHw.Controls[4]
$lblDetectRam = $cardHw.Controls[6]
$lblDetectOs = $cardHw.Controls[8]
$lblDetectLaptop = $cardHw.Controls[10]
$lblDetectAdmin = $cardHw.Controls[12]

$cardGame = New-Card $pageDetect '游戏路径 / 只读体检'
$cardGame.Dock = 'None'
$cardGame.Size = New-Object System.Drawing.Size(440, 220)
$cardGame.Location = New-Object System.Drawing.Point(460, 52)
$cardGame.Anchor = 'Top,Right'
$cardGame.Padding = New-Object System.Windows.Forms.Padding(0)
Add-Label $cardGame '游戏路径' 16 44 90 'muted' | Out-Null
$lblGamePath = Add-Label $cardGame '--' 110 44 290 '' 
$gameChecks = @(
    @('VC++ 运行库', 'ok'),
    @('内存频率', 'ok'),
    @('PCIe 链路', 'attention')
)
$gcY = 120
foreach ($row in $gameChecks) {
    Add-Label $cardGame $row[0] 16 $gcY 100 'muted' | Out-Null
    $badge = Add-Label $cardGame ('● ' + $row[1]) 250 $gcY 140 $row[1]
    $gcY += 34
}
$lblGameCheck1 = $cardGame.Controls[4]
$lblGameCheck2 = $cardGame.Controls[6]
$lblGameCheck3 = $cardGame.Controls[8]

$txtDetect = New-OutputBox $pageDetect 4 286 920 400
$txtDetect.Anchor = 'Top,Bottom,Left,Right'

# =========================================================
# Tab 2: 优化
# =========================================================
$pageOpt = New-Object System.Windows.Forms.Panel
$pageOpt.Dock = 'Fill'
$pageOpt.Visible = $false
$contentHost.Controls.Add($pageOpt)
$script:Pages['opt'] = $pageOpt

$lblOptTitle = Add-Label $pageOpt '优化' 4 4 200 32 'pageTitle'
$btnApply = New-RoundedButton $pageOpt '应用' 110 36 'primary'
$btnApply.Location = New-Object System.Drawing.Point($uiWidth - 260, 8)
$btnApply.Anchor = 'Top,Right'
$pageOpt.Controls.Add($btnApply)
$btnRestore = New-RoundedButton $pageOpt '还原全部' 120 36 'danger'
$btnRestore.Location = New-Object System.Drawing.Point($uiWidth - 140, 8)
$btnRestore.Anchor = 'Top,Right'
$pageOpt.Controls.Add($btnRestore)

$cardPreset = New-Card $pageOpt '预设选择'
$cardPreset.Dock = 'None'
$cardPreset.Size = New-Object System.Drawing.Size(920, 92)
$cardPreset.Location = New-Object System.Drawing.Point(4, 52)
$cardPreset.Anchor = 'Top,Left,Right'
$radioFull = New-Object System.Windows.Forms.RadioButton
$radioFull.Text = 'full'
$radioFull.Location = New-Object System.Drawing.Point(20, 40)
$radioFull.Size = New-Object System.Drawing.Size(90, 24)
$cardPreset.Controls.Add($radioFull)
$radioBalanced = New-Object System.Windows.Forms.RadioButton
$radioBalanced.Text = 'balanced'
$radioBalanced.Location = New-Object System.Drawing.Point(120, 40)
$radioBalanced.Size = New-Object System.Drawing.Size(110, 24)
$radioBalanced.Checked = $true
$cardPreset.Controls.Add($radioBalanced)
$radioSafeOnly = New-Object System.Windows.Forms.RadioButton
$radioSafeOnly.Text = 'safe-only'
$radioSafeOnly.Location = New-Object System.Drawing.Point(240, 40)
$radioSafeOnly.Size = New-Object System.Drawing.Size(110, 24)
$cardPreset.Controls.Add($radioSafeOnly)
$radioCustom = New-Object System.Windows.Forms.RadioButton
$radioCustom.Text = '自定义勾选'
$radioCustom.Location = New-Object System.Drawing.Point(360, 40)
$radioCustom.Size = New-Object System.Drawing.Size(120, 24)
$cardPreset.Controls.Add($radioCustom)
$chkConsent = New-Object System.Windows.Forms.CheckBox
$chkConsent.Text = '我已阅读并同意执行上述优化项（写前自动备份，可还原）'
$chkConsent.Location = New-Object System.Drawing.Point(20, 66)
$chkConsent.Size = New-Object System.Drawing.Size(520, 24)
$cardPreset.Controls.Add($chkConsent)

$cardOptList = New-Card $pageOpt '优化项'
$cardOptList.Dock = 'None'
$cardOptList.Size = New-Object System.Drawing.Size(430, 420)
$cardOptList.Location = New-Object System.Drawing.Point(4, 160)
$cardOptList.Anchor = 'Top,Bottom,Left'
$cardOptList.Padding = New-Object System.Windows.Forms.Padding(12, 38, 12, 12)
$clbItems = New-Object System.Windows.Forms.CheckedListBox
$clbItems.Dock = 'Fill'
$clbItems.Padding = New-Object System.Windows.Forms.Padding(12)
$clbItems.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$cardOptList.Controls.Add($clbItems)
$clbItems.BringToFront()

$cardOptResult = New-Card $pageOpt '执行结果'
$cardOptResult.Dock = 'None'
$cardOptResult.Size = New-Object System.Drawing.Size(470, 420)
$cardOptResult.Location = New-Object System.Drawing.Point(448, 160)
$cardOptResult.Anchor = 'Top,Bottom,Left,Right'
$txtOpt = New-OutputBox $cardOptResult 12 36 440 360
$txtOpt.Anchor = 'Top,Bottom,Left,Right'
$txtOpt.BringToFront()

# =========================================================
# Tab 3: A/B 实验
# =========================================================
$pageAB = New-Object System.Windows.Forms.Panel
$pageAB.Dock = 'Fill'
$pageAB.Visible = $false
$contentHost.Controls.Add($pageAB)
$script:Pages['ab'] = $pageAB

$lblABTitle = Add-Label $pageAB 'A/B 实验' 4 4 200 32 'pageTitle'
$btnOpenExp = New-RoundedButton $pageAB '打开实验目录' 140 36 ''
$btnOpenExp.Location = New-Object System.Drawing.Point($uiWidth - 150, 8)
$btnOpenExp.Anchor = 'Top,Right'
$pageAB.Controls.Add($btnOpenExp)

$btnBase = New-RoundedButton $pageAB '1. 基线' 130 36 ''
$btnBase.Location = New-Object System.Drawing.Point(4, 52)
$pageAB.Controls.Add($btnBase)
$btnG1 = New-RoundedButton $pageAB '2. group-1' 130 36 ''
$btnG1.Location = New-Object System.Drawing.Point(142, 52)
$pageAB.Controls.Add($btnG1)
$btnG2 = New-RoundedButton $pageAB '3. group-2' 130 36 ''
$btnG2.Location = New-Object System.Drawing.Point(280, 52)
$pageAB.Controls.Add($btnG2)
$btnG3 = New-RoundedButton $pageAB '4. group-3' 130 36 ''
$btnG3.Location = New-Object System.Drawing.Point(418, 52)
$pageAB.Controls.Add($btnG3)
$btnReport = New-RoundedButton $pageAB '5. 报告' 130 36 ''
$btnReport.Location = New-Object System.Drawing.Point(556, 52)
$pageAB.Controls.Add($btnReport)

$cardABStatus = New-Card $pageAB ''
$cardABStatus.Dock = 'None'
$cardABStatus.Size = New-Object System.Drawing.Size(920, 64)
$cardABStatus.Location = New-Object System.Drawing.Point(4, 104)
$cardABStatus.Tag = 'banner'
$cardABStatus.Anchor = 'Top,Left,Right'
$lblABStatus = Add-Label $cardABStatus '就绪。点击上方步骤开始采样。' 16 18 700 28 ''
$lblABStatus.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10, [System.Drawing.FontStyle]::Bold)

$cardABResults = New-Card $pageAB '采样结果'
$cardABResults.Dock = 'None'
$cardABResults.Size = New-Object System.Drawing.Size(920, 170)
$cardABResults.Location = New-Object System.Drawing.Point(4, 184)
$cardABResults.Anchor = 'Top,Left,Right'
$txtAB = New-OutputBox $cardABResults 12 36 890 120

$cardABRaw = New-Card $pageAB '原始输出'
$cardABRaw.Dock = 'None'
$cardABRaw.Size = New-Object System.Drawing.Size(920, 210)
$cardABRaw.Location = New-Object System.Drawing.Point(4, 366)
$cardABRaw.Anchor = 'Top,Bottom,Left,Right'
$txtABRaw = New-OutputBox $cardABRaw 12 36 890 160
$txtABRaw.Anchor = 'Top,Bottom,Left,Right'

# =========================================================
# Tab 4: 朋友测试
# =========================================================
$pageFriend = New-Object System.Windows.Forms.Panel
$pageFriend.Dock = 'Fill'
$pageFriend.Visible = $false
$contentHost.Controls.Add($pageFriend)
$script:Pages['friend'] = $pageFriend

$lblFriendTitle = Add-Label $pageFriend '朋友测试' 4 4 200 32 'pageTitle'
$btnFriendGen = New-RoundedButton $pageFriend '生成记录表' 120 36 'primary'
$btnFriendGen.Location = New-Object System.Drawing.Point($uiWidth - 270, 8)
$btnFriendGen.Anchor = 'Top,Right'
$pageFriend.Controls.Add($btnFriendGen)
$btnFriendOpen = New-RoundedButton $pageFriend '打开输出目录' 130 36 ''
$btnFriendOpen.Location = New-Object System.Drawing.Point($uiWidth - 142, 8)
$btnFriendOpen.Anchor = 'Top,Right'
$pageFriend.Controls.Add($btnFriendOpen)

$cardFriendForm = New-Card $pageFriend '表单'
$cardFriendForm.Dock = 'None'
$cardFriendForm.Size = New-Object System.Drawing.Size(440, 350)
$cardFriendForm.Location = New-Object System.Drawing.Point(4, 52)
$cardFriendForm.Anchor = 'Top,Left'
Add-Label $cardFriendForm '昵称' 16 44 90 'muted' | Out-Null
$txtFName = New-FieldTextBox $cardFriendForm 110 42 300 26
Add-Label $cardFriendForm '场景/画质/设置' 16 78 110 'muted' | Out-Null
$txtFScene = New-FieldTextBox $cardFriendForm 110 76 300 26
Add-Label $cardFriendForm '优化前平均 FPS' 16 112 110 'muted' | Out-Null
$txtFBeforeAvg = New-FieldTextBox $cardFriendForm 110 110 140 26
Add-Label $cardFriendForm '优化前 1% low' 260 112 110 'muted' | Out-Null
$txtFBeforeP1 = New-FieldTextBox $cardFriendForm 340 110 70 26
Add-Label $cardFriendForm '优化后平均 FPS' 16 146 110 'muted' | Out-Null
$txtFAfterAvg = New-FieldTextBox $cardFriendForm 110 144 140 26
Add-Label $cardFriendForm '优化后 1% low' 260 146 110 'muted' | Out-Null
$txtFAfterP1 = New-FieldTextBox $cardFriendForm 340 144 70 26
Add-Label $cardFriendForm '备注' 16 182 90 'muted' | Out-Null
$txtFNotes = New-FieldTextBox $cardFriendForm 110 180 300 64
$txtFNotes.Multiline = $true
$txtFNotes.AcceptsReturn = $true
$txtFNotes.ScrollBars = 'Vertical'

$cardFriendPreview = New-Card $pageFriend 'Markdown 预览'
$cardFriendPreview.Dock = 'None'
$cardFriendPreview.Size = New-Object System.Drawing.Size(460, 350)
$cardFriendPreview.Location = New-Object System.Drawing.Point(460, 52)
$cardFriendPreview.Anchor = 'Top,Right'
$txtFriend = New-OutputBox $cardFriendPreview 12 36 430 300
$txtFriend.Anchor = 'Top,Bottom,Left,Right'

# =========================================================
# Tab 5: 备份 / 日志
# =========================================================
$pageBackup = New-Object System.Windows.Forms.Panel
$pageBackup.Dock = 'Fill'
$pageBackup.Visible = $false
$contentHost.Controls.Add($pageBackup)
$script:Pages['backup'] = $pageBackup

$lblBackupTitle = Add-Label $pageBackup '备份 / 日志' 4 4 200 32 'pageTitle'
$btnListBackup = New-RoundedButton $pageBackup '列出可还原项' 140 36 ''
$btnListBackup.Location = New-Object System.Drawing.Point(4, 52)
$pageBackup.Controls.Add($btnListBackup)
$btnRestoreAll2 = New-RoundedButton $pageBackup '还原全部' 120 36 'danger'
$btnRestoreAll2.Location = New-Object System.Drawing.Point(152, 52)
$pageBackup.Controls.Add($btnRestoreAll2)
$btnOpenBackup = New-RoundedButton $pageBackup '打开备份目录' 140 36 ''
$btnOpenBackup.Location = New-Object System.Drawing.Point(280, 52)
$pageBackup.Controls.Add($btnOpenBackup)
$btnOpenGuiTmp = New-RoundedButton $pageBackup '打开 GUI 临时目录' 150 36 ''
$btnOpenGuiTmp.Location = New-Object System.Drawing.Point(428, 52)
$pageBackup.Controls.Add($btnOpenGuiTmp)

$cardBackupOutput = New-Card $pageBackup '日志 / JSON 输出'
$cardBackupOutput.Dock = 'None'
$cardBackupOutput.Size = New-Object System.Drawing.Size(920, 500)
$cardBackupOutput.Location = New-Object System.Drawing.Point(4, 104)
$cardBackupOutput.Anchor = 'Top,Bottom,Left,Right'
$txtBackup = New-OutputBox $cardBackupOutput 12 36 890 450
$txtBackup.Anchor = 'Top,Bottom,Left,Right'

# ---------------------------------------------------------------------------
# 事件绑定（功能逻辑保持原样，仅迁移到新控件）
# ---------------------------------------------------------------------------
$btnDetectRun.Add_Click({
    Show-Text $txtDetect '正在检测...'
    $r = Invoke-GuiScript 'detect' $Engine @('-Detect','-Json')
    if ($r.exitCode -ne 0) {
        Show-Result $txtDetect $r '检测失败'
        return
    }
    $data = ConvertFrom-JsonSafe $r.output
    if (-not $data) {
        Show-Result $txtDetect $r '检测结果解析失败'
        return
    }
    $script:DetectData = $data
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('== 硬件 ==')
    [void]$sb.AppendLine("CPU: $($data.hardware.cpu)")
    [void]$sb.AppendLine("GPU: $($data.hardware.gpu)")
    [void]$sb.AppendLine("内存: $($data.hardware.ramGB) GB")
    [void]$sb.AppendLine("系统: $($data.hardware.os)")
    [void]$sb.AppendLine("笔记本: $($data.hardware.isLaptop)  管理员: $($data.hardware.isAdmin)")
    [void]$sb.AppendLine("游戏路径: $($data.gamePath)")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('== 体检 ==')
    foreach ($c in @($data.checks)) {
        [void]$sb.AppendLine("[$($c.status)] $($c.name): $($c.message)")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("优化项共 $($data.items.Count) 项，详情见原始 JSON：")
    [void]$sb.AppendLine($r.output)
    Show-Text $txtDetect $sb.ToString()
    # 同步硬件卡片
    if ($data.hardware) {
        $lblDetectCpu.Text = [string]$data.hardware.cpu
        $lblDetectGpu.Text = [string]$data.hardware.gpu
        $lblDetectRam.Text = "$($data.hardware.ramGB) GB"
        $lblDetectOs.Text = [string]$data.hardware.os
        $lblDetectLaptop.Text = if ($data.hardware.isLaptop) { '是' } else { '否' }
        $lblDetectAdmin.Text = if ($data.hardware.isAdmin) { '是' } else { '否' }
    }
    if ($data.gamePath) { $lblGamePath.Text = [string]$data.gamePath }
    $checks = @()
    if ($data.checks) { $checks = @($data.checks) }
    if ($checks.Count -ge 1) { $lblGameCheck1.Text = '● ' + $checks[0].status + ' · ' + $checks[0].message }
    if ($checks.Count -ge 2) { $lblGameCheck2.Text = '● ' + $checks[1].status + ' · ' + $checks[1].message }
    if ($checks.Count -ge 3) { $lblGameCheck3.Text = '● ' + $checks[2].status + ' · ' + $checks[2].message }
})

$btnDetectLoad.Add_Click({
    if (-not $script:DetectData) {
        [System.Windows.Forms.MessageBox]::Show($form, '请先在检测页运行一次检测。', '提示')
        return
    }
    $script:ItemIdList = @()
    $clbItems.Items.Clear()
    foreach ($it in @($script:DetectData.items)) {
        [void]$clbItems.Items.Add("$($it.id)  -  $($it.description)")
        $script:ItemIdList += [string]$it.id
    }
    Show-Page 'opt'
    [System.Windows.Forms.MessageBox]::Show($form, "已加载 $($script:ItemIdList.Count) 个优化项到优化页。", '提示')
})

$btnApply.Add_Click({
    if (-not $chkConsent.Checked) {
        [System.Windows.Forms.MessageBox]::Show($form, '请先勾选同意说明，再执行应用。', '未确认')
        return
    }
    $args = @()
    if ($radioFull.Checked) { $args = @('-Apply','-Preset','full','-Force','-Json') }
    elseif ($radioBalanced.Checked) { $args = @('-Apply','-Preset','balanced','-Force','-Json') }
    elseif ($radioSafeOnly.Checked) { $args = @('-Apply','-Preset','safe-only','-Force','-Json') }
    else {
        $ids = @()
        foreach ($i in $clbItems.CheckedIndices) {
            if ($i -lt $script:ItemIdList.Count) { $ids += $script:ItemIdList[$i] }
        }
        if ($ids.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show($form, '请勾选至少一个优化项。', '提示')
            return
        }
        $args = @('-Apply','-Items',($ids -join ','),'-Force','-Json')
    }
    Show-Text $txtOpt '正在应用...'
    $r = Invoke-GuiScript 'apply' $Engine $args
    if ($r.exitCode -ne 0) {
        Show-Result $txtOpt $r '应用失败'
        return
    }
    $j = ConvertFrom-JsonSafe $r.output
    if ($j) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("summary: $($j.summary)")
        [void]$sb.AppendLine("backupFile: $($j.backupFile)")
        [void]$sb.AppendLine("reboot: $($j.reboot -join ', ')")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- 原始 JSON ---')
        [void]$sb.AppendLine($r.output)
        Show-Text $txtOpt $sb.ToString()
    } else {
        Show-Result $txtOpt $r '应用完成'
    }
})

$btnRestore.Add_Click({
    $ans = [System.Windows.Forms.MessageBox]::Show($form, '确定要还原全部已备份的项目吗？', '还原确认', 'YesNo', 'Warning')
    if ($ans -ne 'Yes') { return }
    Show-Text $txtOpt '正在还原...'
    $r = Invoke-GuiScript 'restore' $Engine @('-Restore','-Json')
    if ($r.exitCode -ne 0) {
        Show-Result $txtOpt $r '还原失败'
        return
    }
    $j = ConvertFrom-JsonSafe $r.output
    if ($j) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("summary: $($j.summary)")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- 原始 JSON ---')
        [void]$sb.AppendLine($r.output)
        Show-Text $txtOpt $sb.ToString()
    } else {
        Show-Result $txtOpt $r '还原完成'
    }
})

$btnBase.Add_Click({
    Show-Text $txtAB '基线采样中（3 次，每次约 90 秒），请保持游戏场景固定...'
    $lblABStatus.Text = '采样中，请保持场景固定...'
    $r = Invoke-GuiScript 'ab_baseline' $Tuning @('-Baseline','-Json')
    Show-Result $txtAB $r '基线结果'
    if ($r.output) { Show-Text $txtABRaw $r.output }
    $lblABStatus.Text = '基线完成，可继续下一步。'
})
$btnG1.Add_Click({
    Show-Text $txtAB 'group-1 测试中（3 次采样），请保持场景固定...'
    $lblABStatus.Text = '采样中：group-1，请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g1' $Tuning @('-Test','-Group','group-1','-Json')
    Show-Result $txtAB $r 'group-1 结果'
    if ($r.output) { Show-Text $txtABRaw $r.output }
    $lblABStatus.Text = 'group-1 完成，可继续下一步。'
})
$btnG2.Add_Click({
    Show-Text $txtAB 'group-2 测试中（3 次采样），请保持场景固定...'
    $lblABStatus.Text = '采样中：group-2，请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g2' $Tuning @('-Test','-Group','group-2','-Json')
    Show-Result $txtAB $r 'group-2 结果'
    if ($r.output) { Show-Text $txtABRaw $r.output }
    $lblABStatus.Text = 'group-2 完成，可继续下一步。'
})
$btnG3.Add_Click({
    Show-Text $txtAB 'group-3 测试中（3 次采样），请保持场景固定...'
    $lblABStatus.Text = '采样中：group-3，请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g3' $Tuning @('-Test','-Group','group-3','-Json')
    Show-Result $txtAB $r 'group-3 结果'
    if ($r.output) { Show-Text $txtABRaw $r.output }
    $lblABStatus.Text = 'group-3 完成，可继续下一步。'
})
$btnReport.Add_Click({
    Show-Text $txtAB '正在生成报告...'
    $lblABStatus.Text = '正在生成报告...'
    $r = Invoke-GuiScript 'ab_report' $Tuning @('-Report','-Json')
    Show-Result $txtAB $r '报告'
    if ($r.output) { Show-Text $txtABRaw $r.output }
    $lblABStatus.Text = '报告已生成。'
})
$btnOpenExp.Add_Click({
    $dir = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\experiment'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Start-Process explorer.exe $dir
})

$btnFriendGen.Add_Click({
    if (-not $txtFScene.Text) {
        [System.Windows.Forms.MessageBox]::Show($form, '请填写场景/画质/设置。', '提示')
        return
    }
    $outDir = Join-Path $env:TEMP 'delta-friend-test-out'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $name = $txtFName.Text
    if (-not $name) { $name = $env:USERNAME }
    $args = @(
        '-Name', $name,
        '-Scene', $txtFScene.Text,
        '-BeforeAvgFps', (if ($txtFBeforeAvg.Text) { $txtFBeforeAvg.Text } else { '0' }),
        '-BeforeP1Low', (if ($txtFBeforeP1.Text) { $txtFBeforeP1.Text } else { '0' }),
        '-AfterAvgFps', (if ($txtFAfterAvg.Text) { $txtFAfterAvg.Text } else { '0' }),
        '-AfterP1Low', (if ($txtFAfterP1.Text) { $txtFAfterP1.Text } else { '0' }),
        '-Notes', $txtFNotes.Text,
        '-OutDir', $outDir,
        '-NoPrompt'
    )
    Show-Text $txtFriend '正在生成记录表...'
    $r = Invoke-GuiScript 'friend_test' $FriendTest $args
    if ($r.exitCode -ne 0) {
        Show-Result $txtFriend $r '生成失败'
        return
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('生成完成。输出目录: ' + $outDir)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('--- 脚本输出 ---')
    [void]$sb.AppendLine($r.output)
    $latest = Get-ChildItem $outDir -Filter '*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- ' + $latest.Name + ' ---')
        [void]$sb.AppendLine([System.IO.File]::ReadAllText($latest.FullName))
    }
    Show-Text $txtFriend $sb.ToString()
})

$btnFriendOpen.Add_Click({
    $outDir = Join-Path $env:TEMP 'delta-friend-test-out'
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    Start-Process explorer.exe $outDir
})

$btnListBackup.Add_Click({
    $r = Invoke-GuiScript 'list_restore' $Engine @('-ListRestoreItems','-Json')
    Show-Result $txtBackup $r '可还原项'
})

$btnRestoreAll2.Add_Click({
    $ans = [System.Windows.Forms.MessageBox]::Show($form, '确定要还原全部已备份的项目吗？', '还原确认', 'YesNo', 'Warning')
    if ($ans -ne 'Yes') { return }
    Show-Text $txtBackup '正在还原...'
    $r = Invoke-GuiScript 'restore2' $Engine @('-Restore','-Json')
    Show-Result $txtBackup $r '还原结果'
})

$btnOpenBackup.Add_Click({
    $dir = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\backup'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Start-Process explorer.exe $dir
})

$btnOpenGuiTmp.Add_Click({
    if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir -Force | Out-Null }
    Start-Process explorer.exe $TempDir
})

# ---------------------------------------------------------------------------
# 跟随系统主题：定时检测，系统切换时自动应用
# ---------------------------------------------------------------------------
$themeTimer = New-Object System.Windows.Forms.Timer
$themeTimer.Interval = 2000
$themeTimer.Add_Tick({
    if ($script:ThemeMode -eq 'system') {
        $sys = Get-SystemTheme
        if ($script:Theme -ne $script:Palettes[$sys]) {
            Invoke-ApplyTheme 'system'
        }
    }
})
$themeTimer.Start()

# ---------------------------------------------------------------------------
# 启动
# ---------------------------------------------------------------------------
Invoke-ApplyTheme $script:ThemeMode
Show-Page 'detect'

if ($SmokeTest) {
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $form.Close()
    return
}

[System.Windows.Forms.Application]::Run($form)
