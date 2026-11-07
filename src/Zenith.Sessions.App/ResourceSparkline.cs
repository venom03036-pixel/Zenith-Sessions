using System.Windows;
using System.Windows.Media;

namespace Zenith.Sessions.App;

public readonly record struct ResourceChartPoint(DateTimeOffset ObservedUtc,double? Value,bool IsGap=false);

/// <summary>Bounded historical drawing only: no timer, animation, storage or sampling.</summary>
public sealed class ResourceSparkline : FrameworkElement
{
    public const int MaximumPoints=120;
    public static readonly DependencyProperty PointsProperty=DependencyProperty.Register(nameof(Points),typeof(IReadOnlyList<ResourceChartPoint>),typeof(ResourceSparkline),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty=DependencyProperty.Register(nameof(LineBrush),typeof(Brush),typeof(ResourceSparkline),new FrameworkPropertyMetadata(Brushes.Teal,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty=DependencyProperty.Register(nameof(GridBrush),typeof(Brush),typeof(ResourceSparkline),new FrameworkPropertyMetadata(Brushes.DimGray,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CeilingProperty=DependencyProperty.Register(nameof(Ceiling),typeof(double),typeof(ResourceSparkline),new FrameworkPropertyMetadata(double.NaN,FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<ResourceChartPoint>? Points {get=>(IReadOnlyList<ResourceChartPoint>?)GetValue(PointsProperty);set=>SetValue(PointsProperty,value);}
    public Brush LineBrush {get=>(Brush)GetValue(LineBrushProperty);set=>SetValue(LineBrushProperty,value);}
    public Brush GridBrush {get=>(Brush)GetValue(GridBrushProperty);set=>SetValue(GridBrushProperty,value);}
    public double Ceiling {get=>(double)GetValue(CeilingProperty);set=>SetValue(CeilingProperty,value);}

    public ResourceSparkline() {Focusable=false;IsHitTestVisible=false;SnapsToDevicePixels=true;ClipToBounds=true;}
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if(!double.IsFinite(ActualWidth)||!double.IsFinite(ActualHeight)||ActualWidth<2||ActualHeight<2) return;
        double width=ActualWidth-2,height=ActualHeight-2;
        var grid=new Pen(GridBrush,0.5);
        for(int line=1;line<=3;line++) drawing.DrawLine(grid,new(1,1+height*line/4),new(1+width,1+height*line/4));
        var paths=BuildSegments(Points,width,height,Ceiling);
        if(paths.Count==0) return;
        var stroke=new Pen(LineBrush,1.7) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        foreach(var segment in paths)
        {
            if(segment.Count==1) {drawing.DrawEllipse(LineBrush,null,segment[0],1.6,1.6);continue;}
            var geometry=new StreamGeometry();
            using(var context=geometry.Open()) {context.BeginFigure(segment[0],false,false);for(int point=1;point<segment.Count;point++) context.LineTo(segment[point],true,false);}
            geometry.Freeze();drawing.DrawGeometry(null,stroke,geometry);
        }
    }

    /// <summary>Pure bounded layout, also exercised without a window. Unknowns and long gaps never join.</summary>
    internal static IReadOnlyList<IReadOnlyList<Point>> BuildSegments(IReadOnlyList<ResourceChartPoint>? points,double width,double height,double ceiling=double.NaN)
    {
        int total=points?.Count??0;
        if(points is null||total<=0||!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0) return Array.Empty<IReadOnlyList<Point>>();
        int count=Math.Min(total,MaximumPoints),first=total-count;
        long end=0;double maximum=0;
        for(int index=first;index<total;index++)
        {
            var point=points[index];end=Math.Max(end,point.ObservedUtc.UtcTicks);
            if(!point.IsGap && point.Value is { } value && double.IsFinite(value)&&value>=0) maximum=Math.Max(maximum,value);
        }
        if(end==0) return Array.Empty<IReadOnlyList<Point>>();
        long begin=Math.Max(0,end-TimeSpan.FromMinutes(2).Ticks);
        double range=Math.Max(1,end-begin);
        double limit=double.IsFinite(ceiling)&&ceiling>0?ceiling:maximum>0?maximum:1;
        var segments=new List<IReadOnlyList<Point>>();List<Point>? active=null;long previous=0;
        for(int index=first;index<total;index++)
        {
            var point=points[index];long time=point.ObservedUtc.UtcTicks;
            if(point.IsGap||point.Value is not { } value||!double.IsFinite(value)||value<0||time<begin||time>end||time<=previous)
            {active=null;previous=Math.Max(previous,time);continue;}
            if(active is null||time-previous>TimeSpan.FromSeconds(15).Ticks) {active=[];segments.Add(active);}
            active.Add(new(1+(time-begin)/range*width,1+(1-Math.Clamp(value/limit,0,1))*height));previous=time;
        }
        return segments;
    }
}
