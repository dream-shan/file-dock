# Run with Windows PowerShell -STA. Uses the SVG path as the editable master.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
[xml]$svg = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FileDock.svg') -Raw -Encoding UTF8
$geometry = [Windows.Media.Geometry]::Parse('F0 ' + $svg.svg.path.d)
$sizes = @(16,20,24,32,40,48,64,128,256)
foreach ($variant in @(@{Name='dark';Color='#59616B'},@{Name='light';Color='#F2F3F5'})) {
    $frames = @()
    foreach ($size in $sizes) {
        $drawing = [Windows.Media.DrawingVisual]::new()
        $context = $drawing.RenderOpen()
        $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 32.0, $size / 32.0))
        $context.DrawGeometry([Windows.Media.BrushConverter]::new().ConvertFromString($variant.Color), $null, $geometry)
        $context.Pop(); $context.Close()
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($drawing)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $memory = [IO.MemoryStream]::new()
        try { $encoder.Save($memory); $bytes=$memory.ToArray() } finally { $memory.Dispose() }
        $frames += ,$bytes
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot ('FileDock-'+$variant.Name+'.png')),$bytes) }
    }
    $file=[IO.File]::Create((Join-Path $PSScriptRoot ('FileDock-'+$variant.Name+'.ico')))
    $writer=[IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++) {
            $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame)}
    } finally { $writer.Dispose(); $file.Dispose() }
}
