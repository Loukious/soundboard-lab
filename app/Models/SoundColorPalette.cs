namespace SoundboardLab.Models;

public static class SoundColorPalette
{
    // Twenty-one populated slots and three spare slots in the reference palette.
    public static List<string> CreateDefaultSlots() => [
        "#34A0D6", "#0465B6", "#094E7C", "#2D9C89", "#84B940", "#4DB05C", "#33753D",
        "#F2A840", "#E98022", "#C9571C", "#C0A172", "#964B00", "#F0E8C4", "#F2E71F",
        "#FFAEC9", "#FF00FE", "#C81386", "#D37AFF", "#7300AB", "#D01212", "#A20002",
        "", "", ""
    ];
}
