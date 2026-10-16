param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$large=[Drawing.Bitmap]::new(1024,1024,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($large)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.ScaleTransform(4,4)
$gradient=[Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,14),[Drawing.Point]::new(0,242),[Drawing.ColorTranslator]::FromHtml('#ff3359'),[Drawing.ColorTranslator]::FromHtml('#cd0030'))
$outline=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#a40026'),2)
$graphics.FillEllipse($gradient,14,14,228,228)
$graphics.DrawEllipse($outline,14,14,228,228)
foreach($bar in @(@(65,111,34),@(81,99,58),@(97,87,82))){
 $shape=[Drawing.Drawing2D.GraphicsPath]::new();$x=$bar[0];$y=$bar[1];$height=$bar[2]
 $shape.AddArc($x,$y,8,8,180,180);$shape.AddArc($x,($y+$height-8),8,8,0,180);$shape.CloseFigure();$graphics.FillPath([Drawing.Brushes]::White,$shape);$shape.Dispose()
}
$triangle=[Drawing.Point[]]@([Drawing.Point]::new(123,87),[Drawing.Point]::new(184,128),[Drawing.Point]::new(123,169))
$graphics.FillPolygon([Drawing.Brushes]::White,$triangle)
$graphics.Dispose();$gradient.Dispose();$outline.Dispose()
$frames=@()
foreach($size in @(16,20,24,32,40,48,64,128,256)){
 $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
 $draw=[Drawing.Graphics]::FromImage($bitmap);$draw.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$draw.DrawImage($large,0,0,$size,$size);$draw.Dispose()
 $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
 $frames+=@{Size=$size;Bytes=$stream.ToArray()}
 if($size -eq 256){$bitmap.Save((Join-Path $PSScriptRoot 'app-icon.png'),[Drawing.Imaging.ImageFormat]::Png)}
 $stream.Dispose();$bitmap.Dispose()
}
$large.Dispose()
$output=[IO.File]::Create((Join-Path $PSScriptRoot 'app-icon.ico'));$writer=[IO.BinaryWriter]::new($output)
try{
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count);$offset=6+16*$frames.Count
 foreach($frame in $frames){$dimension=if($frame.Size -eq 256){0}else{$frame.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.Bytes.Length}
 foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
}finally{$writer.Dispose();$output.Dispose()}
