Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path $PSScriptRoot '..\src\Lantern.App\Assets'
$bmp = [System.Drawing.Bitmap]::new(256,256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$path=[System.Drawing.Drawing2D.GraphicsPath]::new()
$path.AddArc(8,8,52,52,180,90);$path.AddArc(196,8,52,52,270,90);$path.AddArc(196,196,52,52,0,90);$path.AddArc(8,196,52,52,90,90);$path.CloseFigure()
$bg=[System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#151E2C'))
$amber=[System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFAE56'))
$light=[System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFF0CB'))
$pen=[System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#FFAE56'),13)
$g.FillPath($bg,$path)
$g.DrawArc($pen,100,35,56,58,180,180)
$pts=[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(86,85),[System.Drawing.PointF]::new(170,85),[System.Drawing.PointF]::new(194,179),[System.Drawing.PointF]::new(170,211),[System.Drawing.PointF]::new(86,211),[System.Drawing.PointF]::new(62,179))
$g.FillPolygon($amber,$pts)
$pts2=[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(103,105),[System.Drawing.PointF]::new(153,105),[System.Drawing.PointF]::new(170,173),[System.Drawing.PointF]::new(155,190),[System.Drawing.PointF]::new(101,190),[System.Drawing.PointF]::new(86,173))
$g.FillPolygon($bg,$pts2)
$fire=[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(134,113),[System.Drawing.PointF]::new(108,151),[System.Drawing.PointF]::new(126,151),[System.Drawing.PointF]::new(119,180),[System.Drawing.PointF]::new(149,139),[System.Drawing.PointF]::new(132,139))
$g.FillPolygon($light,$fire)
$bmp.Save((Join-Path $assetDir 'lantern.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$sizes=@(16,24,32,48,64,128,256)
$images=@()
foreach($s in $sizes){
 $small=[System.Drawing.Bitmap]::new($s,$s);$sg=[System.Drawing.Graphics]::FromImage($small);$sg.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$sg.DrawImage($bmp,0,0,$s,$s)
 $ms=[System.IO.MemoryStream]::new();$small.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png);$images+=,@($ms.ToArray());$sg.Dispose();$small.Dispose();$ms.Dispose()
}
$stream=[System.IO.File]::Create((Join-Path $assetDir 'lantern.ico'));$w=[System.IO.BinaryWriter]::new($stream)
$w.Write([uint16]0);$w.Write([uint16]1);$w.Write([uint16]$sizes.Count);$offset=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){$dim=if($sizes[$i]-eq 256){0}else{$sizes[$i]};$w.Write([byte]$dim);$w.Write([byte]$dim);$w.Write([byte]0);$w.Write([byte]0);$w.Write([uint16]1);$w.Write([uint16]32);$w.Write([uint32]$images[$i].Count);$w.Write([uint32]$offset);$offset+=$images[$i].Count}
foreach($im in $images){$w.Write([byte[]]$im)}
$w.Dispose();$g.Dispose();$bmp.Dispose();$path.Dispose();$bg.Dispose();$amber.Dispose();$light.Dispose();$pen.Dispose()
