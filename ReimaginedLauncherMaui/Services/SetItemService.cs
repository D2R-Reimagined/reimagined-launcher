using ReimaginedLauncherMaui.Model;

namespace ReimaginedLauncherMaui.Services
{
    internal class SetItemService : ISetItemService
    {
        private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "Resources/GameData/data/global/excel/sets.txt");

        public async Task<IList<SetItem>> GetItems()
        {
            var lines = (await File.ReadAllLinesAsync(_filePath)).Skip(1); // Skip header line

            return lines.Select(line => line.Split('\t'))
                .Select(columns => new SetItem
                {
                    Index = columns[0],
                    Name = columns[1],
                    Version = int.Parse(columns[2]),

                    // Set 2 properties
                    // PCode2a = columns[3],
                    // PParam2a = columns[4],
                    // PMin2a = int.Parse(columns[5]),
                    // PMax2a = int.Parse(columns[6]),
                    //
                    // PCode2b = columns[7],
                    // PParam2b = columns[8],
                    // PMin2b = int.Parse(columns[9]),
                    // PMax2b = int.Parse(columns[10]),
                    //
                    // // Set 3 properties
                    // PCode3a = columns[11],
                    // PParam3a = columns[12],
                    // PMin3a = int.Parse(columns[13]),
                    // PMax3a = int.Parse(columns[14]),
                    //
                    // PCode3b = columns[15],
                    // PParam3b = columns[16],
                    // PMin3b = int.Parse(columns[17]),
                    // PMax3b = int.Parse(columns[18]),
                    //
                    // // Set 4 properties
                    // PCode4a = columns[19],
                    // PParam4a = columns[20],
                    // PMin4a = int.Parse(columns[21]),
                    // PMax4a = int.Parse(columns[22]),
                    //
                    // PCode4b = columns[23],
                    // PParam4b = columns[24],
                    // PMin4b = int.Parse(columns[25]),
                    // PMax4b = int.Parse(columns[26]),
                    //
                    // // Set 5 properties
                    // PCode5a = columns[27],
                    // PParam5a = columns[28],
                    // PMin5a = int.Parse(columns[29]),
                    // PMax5a = int.Parse(columns[30]),
                    //
                    // PCode5b = columns[31],
                    // PParam5b = columns[32],
                    // PMin5b = int.Parse(columns[33]),
                    // PMax5b = int.Parse(columns[34]),
                    //
                    // // Final (F) properties
                    // FCode1 = columns[35],
                    // FParam1 = columns[36],
                    // FMin1 = int.Parse(columns[37]),
                    // FMax1 = int.Parse(columns[38]),
                    //
                    // FCode2 = columns[39],
                    // FParam2 = columns[40],
                    // FMin2 = int.Parse(columns[41]),
                    // FMax2 = int.Parse(columns[42]),
                    //
                    // FCode3 = columns[43],
                    // FParam3 = columns[44],
                    // FMin3 = int.Parse(columns[45]),
                    // FMax3 = int.Parse(columns[46]),
                    //
                    // FCode4 = columns[47],
                    // FParam4 = columns[48],
                    // FMin4 = int.Parse(columns[49]),
                    // FMax4 = int.Parse(columns[50]),
                    //
                    // FCode5 = columns[51],
                    // FParam5 = columns[52],
                    // FMin5 = int.Parse(columns[53]),
                    // FMax5 = int.Parse(columns[54]),
                    //
                    // FCode6 = columns[55],
                    // FParam6 = columns[56],
                    // FMin6 = int.Parse(columns[57]),
                    // FMax6 = int.Parse(columns[58]),
                    //
                    // FCode7 = columns[59],
                    // FParam7 = columns[60],
                    // FMin7 = int.Parse(columns[61]),
                    // FMax7 = int.Parse(columns[62]),
                    //
                    // FCode8 = columns[63],
                    // FParam8 = columns[64],
                    // FMin8 = int.Parse(columns[65]),
                    // FMax8 = int.Parse(columns[66]),

                    // Eol (end of line)
                    //Eol = int.Parse(columns[71])
                })
                .ToList();
        }
    }
}
