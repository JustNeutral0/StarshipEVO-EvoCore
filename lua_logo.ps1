# Draws a Lua logo (planet, hole, moon on a dashed orbit, "Lua") as a PNG for the Lua Computer's look.
#   lua_logo.ps1 <out.png> [-Size 256]          full colour: navy and white on white
#   lua_logo.ps1 <out.png> -Holo                hologram: white on black, the way the stock gate symbols are drawn
#                                               (black is see-through, white glows in the block's paint colour), so
#                                               "Lua" and the hole are cut out of the planet
param([string] $Out, [int] $Size = 256, [switch] $Holo)
Add-Type -AssemblyName System.Drawing

$S = [single]$Size
$R = [single]($S * 68 / 256)   # planet radius
$c = [single]($S / 2)

$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

if ($Holo) {
    $back = [System.Drawing.Color]::Black
    $planet = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cut = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
    $orbitColour = [System.Drawing.Color]::White
    $orbitWidth = [single]($S * 4 / 256)
} else {
    $back = [System.Drawing.Color]::White
    $planet = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 0, 128))
    $cut = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $orbitColour = [System.Drawing.Color]::FromArgb(128, 128, 128)
    $orbitWidth = [single]($S * 3 / 256)
}
$g.Clear($back)

function Disc($brush, [single] $x, [single] $y, [single] $r) {
    $g.FillEllipse($brush, $x - $r, $y - $r, 2 * $r, 2 * $r)
}

# The orbit: a dashed circle through the moon.
$orbit = [single](1.52 * $R)
$pen = New-Object System.Drawing.Pen $orbitColour, $orbitWidth
$pen.DashPattern = [single[]](3, 2)
$g.DrawEllipse($pen, $c - $orbit, $c - $orbit, 2 * $orbit, 2 * $orbit)

Disc $planet $c $c $R
Disc $cut ($c + 0.49 * $R) ($c - 0.49 * $R) (0.26 * $R)
Disc $planet ($c + 1.08 * $R) ($c - 1.08 * $R) (0.26 * $R)

$font = New-Object System.Drawing.Font 'Arial', ([single]($S * 40 / 256)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$box = New-Object System.Drawing.RectangleF ([single]($c - $R)), ([single]($c + 0.05 * $R)), ([single](2 * $R)), ([single](0.7 * $R))
$g.DrawString('Lua', $font, $cut, $box, $fmt)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"wrote $Out"
