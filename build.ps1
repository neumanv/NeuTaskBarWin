# Compila NeuTaskBar con el compilador de C# incluido en Windows (.NET Framework 4.x). No requiere instalar nada.
# Genera en esta misma carpeta: NeuTaskBar.exe (aplicacion) y Uninstall.exe (desinstalador).
param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$assets = Join-Path $root 'assets'
$logoSource = Join-Path $assets 'logo-source.png'
$logoUi = Join-Path $assets 'logo.png'
$icon = Join-Path $assets 'app.ico'
New-Item -ItemType Directory -Force $assets | Out-Null

# --- Recursos graficos a partir del logo (recorte del logo transparente; icono sobre baldosa blanca) ---
if (-not (Test-Path $logoSource)) { throw "Falta $logoSource" }
$needAssets = (-not (Test-Path $logoUi)) -or (-not (Test-Path $icon)) -or
    ((Get-Item $logoSource).LastWriteTimeUtc -gt (Get-Item $icon).LastWriteTimeUtc)
if ($needAssets) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class AssetMaker
{
    public static void Make(string srcPng, string uiPng, string icoPath)
    {
        Bitmap logo;
        using (var src = new Bitmap(srcPng))
        {
            int w = src.Width, h = src.Height;
            var argb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = argb.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] row = new byte[sd.Stride];
            int minX = w, minY = h, maxX = 0, maxY = 0;
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(sd.Scan0, y * sd.Stride), row, 0, sd.Stride);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    int a = row[i + 3];                   // el logo original ya es transparente: se conserva su alfa
                    if (a < 8) { a = 0; row[i] = row[i + 1] = row[i + 2] = 0; }
                    row[i + 3] = (byte)a;
                    if (a > 40) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(dd.Scan0, y * dd.Stride), dd.Stride);
            }
            src.UnlockBits(sd);
            argb.UnlockBits(dd);
            int mx = (int)((maxX - minX) * 0.02) + 2, my = (int)((maxY - minY) * 0.02) + 2;
            var crop = Rectangle.Intersect(rect, new Rectangle(minX - mx, minY - my, maxX - minX + 2 * mx, maxY - minY + 2 * my));
            logo = argb.Clone(crop, PixelFormat.Format32bppArgb);
            argb.Dispose();
        }
        logo.Save(uiPng, ImageFormat.Png);

        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var pngs = new byte[sizes.Length][];
        for (int k = 0; k < sizes.Length; k++)
        {
            int s = sizes[k];
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float r = s * 0.22f, d = r * 2f, inset = s <= 24 ? 0f : s * 0.02f, sz = s - inset * 2f;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(inset, inset, d, d, 180, 90);
                    path.AddArc(inset + sz - d, inset, d, d, 270, 90);
                    path.AddArc(inset + sz - d, inset + sz - d, d, d, 0, 90);
                    path.AddArc(inset, inset + sz - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(Brushes.White, path);
                }
                float lw = s * 0.74f, lh = lw * logo.Height / logo.Width;
                g.DrawImage(logo, (s - lw) / 2f, (s - lh) / 2f, lw, lh);
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs[k] = ms.ToArray(); }
            }
        }
        using (var fs = File.Create(icoPath))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int k = 0; k < sizes.Length; k++)
            {
                int s = sizes[k];
                bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)pngs[k].Length); bw.Write((uint)offset);
                offset += pngs[k].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }
        logo.Dispose();
    }
}
'@
    [AssetMaker]::Make($logoSource, $logoUi, $icon)
    Write-Host "Recursos generados a partir de logo-source.png"
}

# --- Compilacion ---
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$manifest = Join-Path $root 'app.manifest'

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $root 'NeuTaskBar.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug- /warn:4 "/out:$exe" "/win32manifest:$manifest" "/win32icon:$icon" `
    "/resource:$logoUi,logo.png" "/resource:$icon,app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de NeuTaskBar.exe" }

$unSources = @(
    (Join-Path $root 'uninstall\Uninstall.cs'),
    (Join-Path $root 'src\Native.cs'),
    (Join-Path $root 'src\Config.cs'),
    (Join-Path $root 'src\ShellTaskbar.cs')
)
$unExe = Join-Path $root 'Uninstall.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug- /warn:4 "/out:$unExe" "/win32manifest:$manifest" "/win32icon:$icon" `
    /r:System.dll /r:System.Core.dll $unSources
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de Uninstall.exe" }

Write-Host "OK: $exe"
Write-Host "OK: $unExe"

# Registra la app: acceso directo en el menu Inicio (para encontrarla al buscar en Windows) y entrada en Aplicaciones.
Start-Process $exe -ArgumentList '--register' -Wait
if ($Run) { Start-Process $exe }
