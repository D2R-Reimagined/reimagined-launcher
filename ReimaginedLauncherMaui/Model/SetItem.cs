using D2RReimaginedTools.Models;

namespace ReimaginedLauncherMaui.Model;

public record SetItem
{
    public string Index { get; set; }
    public string Name { get; set; }
    public int Version { get; set; }
    public string PCode2a { get; set; }
    public string PParam2a { get; set; }
    public int PMin2a { get; set; }
    public int PMax2a { get; set; }

    public string PCode2b { get; set; }
    public string PParam2b { get; set; }
    public int PMin2b { get; set; }
    public int PMax2b { get; set; }


    public string PCode3a { get; set; }
    public string PParam3a { get; set; }
    public int PMin3a { get; set; }
    public int PMax3a { get; set; }

    public string PCode3b { get; set; }
    public string PParam3b { get; set; }
    public int PMin3b { get; set; }
    public int PMax3b { get; set; }


    public string PCode4a { get; set; }
    public string PParam4a { get; set; }
    public int PMin4a { get; set; }
    public int PMax4a { get; set; }

    public string PCode4b { get; set; }
    public string PParam4b { get; set; }
    public int PMin4b { get; set; }
    public int PMax4b { get; set; }


    public string PCode5a { get; set; }
    public string PParam5a { get; set; }
    public int PMin5a { get; set; }
    public int PMax5a { get; set; }

    public string PCode5b { get; set; }
    public string PParam5b { get; set; }
    public int PMin5b { get; set; }
    public int PMax5b { get; set; }


    public string FCode1 { get; set; }
    public string FParam1 { get; set; }
    public int FMin1 { get; set; }
    public int FMax1 { get; set; }

    public string FCode2 { get; set; }
    public string FParam2 { get; set; }
    public int FMin2 { get; set; }
    public int FMax2 { get; set; }

    public string FCode3 { get; set; }
    public string FParam3 { get; set; }
    public int FMin3 { get; set; }
    public int FMax3 { get; set; }

    public string FCode4 { get; set; }
    public string FParam4 { get; set; }
    public int FMin4 { get; set; }
    public int FMax4 { get; set; }

    public string FCode5 { get; set; }
    public string FParam5 { get; set; }
    public int FMin5 { get; set; }
    public int FMax5 { get; set; }

    public string FCode6 { get; set; }
    public string FParam6 { get; set; }
    public int FMin6 { get; set; }
    public int FMax6 { get; set; }

    public string FCode7 { get; set; }
    public string FParam7 { get; set; }
    public int FMin7 { get; set; }
    public int FMax7 { get; set; }

    public string FCode8 { get; set; }
    public string FParam8 { get; set; }
    public int FMin8 { get; set; }
    public int FMax8 { get; set; }
    public int Eol { get; init; }
    
    public IList<ItemProperty> Properties { get; init; }

}