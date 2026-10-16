using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private Button HelpButton(string id,string title,string description)
    {
        var button=new Button {Style=(Style)FindResource("PreferencesHelpButton")};
        AutomationProperties.SetAutomationId(button,id);AutomationProperties.SetName(button,Localization.Text(title));AutomationProperties.SetHelpText(button,Localization.Text(description));
        var heading=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(0,103,171)),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,6)};
        var body=new TextBlock {TextWrapping=TextWrapping.Wrap};
        var text=new StackPanel {MaxWidth=420};text.Children.Add(heading);text.Children.Add(body);
        var content=new Grid();content.ColumnDefinitions.Add(new(){Width=new(22)});content.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var information=new Grid {Width=16,Height=16,VerticalAlignment=VerticalAlignment.Top};
        information.Children.Add(new System.Windows.Shapes.Ellipse {Fill=new SolidColorBrush(Color.FromRgb(0,103,171))});
        information.Children.Add(new TextBlock {Text="i",Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        AutomationProperties.SetName(information,"Information");content.Children.Add(information);Grid.SetColumn(text,1);content.Children.Add(text);
        var tip=new ToolTip {Content=content,PlacementTarget=button,Placement=PlacementMode.Bottom,Padding=new(8),StaysOpen=true,Style=(Style)FindResource("PreferencesDescriptionToolTip")};
        tip.Opened+=(_,_)=>{heading.Text=Localization.Text(title);body.Text=Localization.Text(description);AutomationProperties.SetHelpText(button,body.Text);};
        button.ToolTip=tip;ToolTipService.SetInitialShowDelay(button,300);ToolTipService.SetShowDuration(button,60000);
        button.Click+=(_,_)=>{button.Focus();tip.IsOpen=!tip.IsOpen;};
        button.MouseLeave+=(_,_)=>tip.IsOpen=false;
        button.LostKeyboardFocus+=(_,_)=>tip.IsOpen=false;
        button.Unloaded+=(_,_)=>tip.IsOpen=false;
        button.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){tip.IsOpen=false;e.Handled=true;}};
        return button;
    }
    private void NormalizationHelpRow(Panel parent,RadioButton option,string id,string title,string description)
    {
        var row=new Grid {HorizontalAlignment=HorizontalAlignment.Left};
        row.ColumnDefinitions.Add(new(){Width=GridLength.Auto,MinWidth=264,SharedSizeGroup="NormalizationOptionLabel"});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        row.Children.Add(option);var help=HelpButton(id,title,description);Grid.SetColumn(help,1);row.Children.Add(help);parent.Children.Add(row);
    }
}
