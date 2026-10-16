using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundboardLab;

internal sealed class IntegerSpinner : UserControl
{
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(int),typeof(IntegerSpinner),
        new FrameworkPropertyMetadata(0,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,(sender,_)=>((IntegerSpinner)sender).RefreshText()));
    public int Value {get=>(int)GetValue(ValueProperty);set=>SetValue(ValueProperty,value);}
    private readonly TextBox input;
    private readonly int minimum,maximum;
    private bool updating;
    private string accepted="0";

    public IntegerSpinner(string property,string label,int minimum,int maximum)
    {
        this.minimum=minimum;this.maximum=maximum;
        Width=64;Height=23;Margin=new(2);HorizontalAlignment=HorizontalAlignment.Left;
        var layout=new Grid();layout.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});layout.ColumnDefinitions.Add(new(){Width=new(15)});
        input=new TextBox {Text="0",BorderThickness=new(0),Margin=new(0),Padding=new(3,1,3,1),MinHeight=0,VerticalContentAlignment=VerticalAlignment.Center};
        AutomationProperties.SetAutomationId(input,property);AutomationProperties.SetName(input,label);
        layout.Children.Add(input);
        var arrows=new Grid();arrows.RowDefinitions.Add(new());arrows.RowDefinitions.Add(new());Grid.SetColumn(arrows,1);layout.Children.Add(arrows);
        for(int row=0;row<2;row++){
            int step=row==0?1:-1;
            var arrow=new RepeatButton {Content=new System.Windows.Shapes.Path {Data=Geometry.Parse(row==0?"M0,3 L3,0 6,3 Z":"M0,0 L3,3 6,0 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,35,74)),Width=6,Height=3},
                Padding=new(0),Margin=new(0),MinWidth=0,MinHeight=0,Focusable=false};
            AutomationProperties.SetName(arrow,(step>0?"Increase ":"Decrease ")+label);
            AutomationProperties.SetAutomationId(arrow,property+(step>0?"Increase":"Decrease"));
            arrow.Click+=(_,_)=>Step(step);Grid.SetRow(arrow,row);arrows.Children.Add(arrow);
        }
        Content=new Border {BorderBrush=new SolidColorBrush(Color.FromRgb(170,170,170)),BorderThickness=new(1),Background=Brushes.White,Child=layout};
        input.PreviewTextInput+=(_,e)=>e.Handled=!Accepts(Replacement(e.Text));
        DataObject.AddPastingHandler(input,(_,e)=>{
            if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)||e.DataObject.GetData(DataFormats.UnicodeText) is not string text||!Accepts(Replacement(text)))e.CancelCommand();
        });
        input.TextChanged+=(_,_)=>{
            if(updating)return;
            if(!Accepts(input.Text)){updating=true;int caret=input.CaretIndex;input.Text=accepted;input.CaretIndex=Math.Min(caret,accepted.Length);updating=false;return;}
            accepted=input.Text;
            if(int.TryParse(accepted,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int value)){
                updating=true;SetCurrentValue(ValueProperty,value);updating=false;
            }
        };
        input.LostKeyboardFocus+=(_,_)=>RefreshText();
        input.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Up or Key.Down){Step(e.Key==Key.Up?1:-1);e.Handled=true;}};
    }
    private string Replacement(string text)=>input.Text.Remove(input.SelectionStart,input.SelectionLength).Insert(input.SelectionStart,text);
    private bool Accepts(string text)=>text.Length==0||(text=="-"&&minimum<0)||
        (text.All(char.IsAsciiDigit)||minimum<0&&text.StartsWith('-')&&text[1..].All(char.IsAsciiDigit))&&
        int.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int value)&&value>=minimum&&value<=maximum;
    private void Step(int amount){SetCurrentValue(ValueProperty,Math.Clamp(Value+amount,minimum,maximum));RefreshText();}
    private void RefreshText()
    {
        if(updating||input is null)return;
        updating=true;accepted=Value.ToString(CultureInfo.InvariantCulture);input.Text=accepted;updating=false;
    }
}
