namespace SoundboardLab.Audio;

public sealed class EditorDocument(float[] samples)
{
    private record Snapshot(float[] Samples,double Start,double End);
    private readonly List<Snapshot> undo=[],redo=[];
    private float[] saved=samples;
    public float[] Samples {get;private set;}=samples;
    public double Duration=>Samples.Length/96000d;
    public double Start {get;set;}
    public double End {get;set;}
    public bool HasSelection=>Math.Abs(End-Start)>1d/48000;
    public double SelectionStart=>Math.Clamp(Math.Min(Start,End),0,Duration);
    public double SelectionEnd=>Math.Clamp(Math.Max(Start,End),0,Duration);
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    public bool Modified=>!ReferenceEquals(saved,Samples);
    private Snapshot Current()=>new(Samples,Start,End);
    private void Restore(Snapshot value){Samples=value.Samples;Start=value.Start;End=value.End;}
    private void Commit(float[] value,double start,double end){undo.Add(Current());if(undo.Count>20)undo.RemoveAt(0);redo.Clear();Samples=value;Start=start;End=end;}
    private (int First,int Last) Range(bool allWhenEmpty=false)=>(!HasSelection&&allWhenEmpty)?(0,Samples.Length):((int)(SelectionStart*48000)*2,(int)(SelectionEnd*48000)*2);
    public void Undo(){if(!CanUndo)return;redo.Add(Current());var value=undo[^1];undo.RemoveAt(undo.Count-1);Restore(value);}
    public void Redo(){if(!CanRedo)return;undo.Add(Current());var value=redo[^1];redo.RemoveAt(redo.Count-1);Restore(value);}
    public void MarkSaved()=>saved=Samples;
    public void SelectAll(){Start=0;End=Duration;}
    public void Deselect(){Start=End=SelectionStart;}
    public void Cut()
    {
        if(!HasSelection)return;var (first,last)=Range();if(last-first>=Samples.Length)throw new InvalidOperationException("The sound must contain at least one frame.");
        var next=new float[Samples.Length-(last-first)];Array.Copy(Samples,0,next,0,first);Array.Copy(Samples,last,next,first,Samples.Length-last);Commit(next,first/96000d,first/96000d);
    }
    public void Crop(){if(!HasSelection)return;var (first,last)=Range();Commit(Samples[first..last],0,(last-first)/96000d);}
    public void Effect(string effect)
    {
        var (first,last)=Range(true);if(last<=first)return;var section=Samples[first..last];
        if(effect=="reverse"){for(int left=0,right=section.Length-2;left<right;left+=2,right-=2){(section[left],section[right])=(section[right],section[left]);(section[left+1],section[right+1])=(section[right+1],section[left+1]);}}
        else {double duration=section.Length/96000d;section=AudioFiles.Edit(section,new(FadeIn:effect=="fade-in"?TimeSpan.FromSeconds(duration):null,FadeOut:effect=="fade-out"?TimeSpan.FromSeconds(duration):null,PeakCeiling:1));}
        var next=(float[])Samples.Clone();Array.Copy(section,0,next,first,section.Length);Commit(next,Start,End);
    }
    public float[] SelectedSamples()=>HasSelection?Samples[Range().First..Range().Last]:Samples;
}
