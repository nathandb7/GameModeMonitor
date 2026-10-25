#requires -Version 5.1
# Regenerates the repository-native rocket/gauge Windows icon. No external tools.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path $PSScriptRoot) 'src\GameModeMonitor.App\Assets'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
public static class GameModeIconBuilder {
    static Color C(string value) { return ColorTranslator.FromHtml(value); }
    static GraphicsPath Rounded() {
        var p=new GraphicsPath();
        p.AddArc(8,8,224,224,180,90); p.AddArc(280,8,224,224,270,90);
        p.AddArc(280,280,224,224,0,90); p.AddArc(8,280,224,224,90,90); p.CloseFigure(); return p;
    }
    static void Fill(Graphics g, string color, Point[] points) {
        using(var b=new SolidBrush(C(color))) g.FillPolygon(b,points);
    }
    public static void Build(string root) {
        using(var image=new Bitmap(512,512,PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(image)) {
            g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=Rounded())
            using(var gradient=new LinearGradientBrush(new Point(0,0),new Point(0,512),C("#0a1426"),C("#172c48")))
            using(var border=new Pen(C("#304a68"),8)) { g.FillPath(gradient,p); g.DrawPath(border,p); }
            using(var track=new Pen(C("#273e58"),20)) {
                track.StartCap=track.EndCap=LineCap.Round; g.DrawArc(track,76,76,360,360,145,250);
            }
            using(var accent=new Pen(C("#36bff4"),20)) {
                accent.StartCap=accent.EndCap=LineCap.Round; g.DrawArc(accent,76,76,360,360,145,158);
            }
            var saved=g.Save(); g.TranslateTransform(256,256); g.RotateTransform(35); g.TranslateTransform(-256,-256);
            using(var flame=new GraphicsPath())
            using(var blue=new SolidBrush(C("#28bff0"))) {
                flame.AddBezier(228,292,222,337,236,365,256,374); flame.AddBezier(256,374,277,352,290,324,284,292); flame.CloseFigure(); g.FillPath(blue,flame);
            }
            using(var flame=new GraphicsPath())
            using(var white=new SolidBrush(C("#d6fbff"))) {
                flame.AddBezier(242,293,237,323,244,336,256,349); flame.AddBezier(256,349,268,335,275,315,270,293); flame.CloseFigure(); g.FillPath(white,flame);
            }
            Fill(g,"#36bff4",new[]{new Point(218,216),new Point(185,263),new Point(191,308),new Point(230,281)});
            Fill(g,"#36bff4",new[]{new Point(294,216),new Point(327,263),new Point(321,308),new Point(282,281)});
            using(var hull=new GraphicsPath())
            using(var gradient=new LinearGradientBrush(new Point(0,75),new Point(0,287),C("#f3fdff"),C("#91dcff"))) {
                hull.AddBezier(256,75,211,131,205,216,221,287); hull.AddLine(221,287,291,287); hull.AddBezier(291,287,307,216,301,131,256,75); hull.CloseFigure(); g.FillPath(gradient,hull);
            }
            using(var glass=new SolidBrush(C("#13273f"))) g.FillEllipse(glass,229,150,54,54);
            using(var ring=new Pen(C("#35bdf3"),7)) g.DrawEllipse(ring,229,150,54,54);
            using(var shine=new SolidBrush(C("#c5f6ff"))) g.FillEllipse(shine,241,163,12,12);
            using(var fin=new Pen(C("#2da7d8"),9)) { fin.StartCap=fin.EndCap=LineCap.Round; g.DrawLine(fin,230,269,282,269); }
            g.Restore(saved);
            image.Save(Path.Combine(root,"GameModeMonitor.png"),ImageFormat.Png);
            int[] sizes={16,20,24,32,40,48,64,128,256}; var frames=new List<byte[]>();
            foreach(var size in sizes) {
                using(var scaled=new Bitmap(size,size,PixelFormat.Format32bppArgb))
                using(var sg=Graphics.FromImage(scaled))
                using(var memory=new MemoryStream()) {
                    sg.InterpolationMode=InterpolationMode.HighQualityBicubic; sg.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    sg.DrawImage(image,new Rectangle(0,0,size,size)); scaled.Save(memory,ImageFormat.Png); frames.Add(memory.ToArray());
                }
            }
            using(var output=File.Create(Path.Combine(root,"GameModeMonitor.ico")))
            using(var writer=new BinaryWriter(output)) {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset=6+16*sizes.Length;
                for(int i=0;i<sizes.Length;i++) {
                    writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset); offset+=frames[i].Length;
                }
                foreach(var frame in frames) writer.Write(frame);
            }
        }
    }
}
'@
[GameModeIconBuilder]::Build($assetRoot)
Write-Output 'Generated rocket/gauge PNG and multi-resolution ICO (16-256 px).'
