using System.Windows;
using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private CategoryEntry CurrentCategory=>selectedCategory is "*" or ""?library.Document.RootCategory:library.Document.Categories.FirstOrDefault(x=>x.Name==selectedCategory)??library.Document.RootCategory;
    private void EditCategory_Click(object sender,RoutedEventArgs e)
    {
        string path=selectedCategory is "*" or ""?"*":selectedCategory;
        var dialog=new CategoryEditWindow(CurrentCategory, (owner, key) => EditHotkey(owner, key, "category:"+path)){Owner=this};
        if(dialog.ShowDialog()!=true)return;
        Guard(()=>{if(path!="*"){int slash=path.LastIndexOf('/');dialog.Value.Name=(slash>=0?path[..(slash+1)]:"")+dialog.Value.Name;}if(dialog.Value.Hotkey!=CurrentCategory.Hotkey)AssignHotkey("category:"+path,dialog.Value.Hotkey);library.UpdateCategory(path,dialog.Value);selectedCategory=path=="*"?"*":dialog.Value.Name;RefreshLibrary();});
    }
}
