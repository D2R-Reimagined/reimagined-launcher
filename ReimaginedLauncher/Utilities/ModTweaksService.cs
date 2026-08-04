using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using D2RReimaginedTools.TextFileParsers;
using ReimaginedLauncher.Utilities.Json;

namespace ReimaginedLauncher.Utilities;

public static class ModTweaksService
{
    private const string ModDirectoryName = "Reimagined";
    private const string DataDirectoryName = "data";
    private const string ExcelDirectoryName = "excel";
    private const string BaseExcelDirectoryName = "base";
    private const string CleanExcelDirectoryName = "excel_launcher_clean";
    private const string HdDirectoryName = "hd";
    private const string ItemsDirectoryName = "items";
    private const string ArmorDirectoryName = "armor";
    private const string HelmetDirectoryName = "helmet";
    private const string CircletDirectoryName = "circlet";
    private const string PeltDirectoryName = "pelt";
    private const string GlobalDirectoryName = "global";
    private const string UiDirectoryName = "ui";
    private const string LayoutsDirectoryName = "layouts";
    private const string MissilesDirectoryName = "missiles";
    private const string MissilesFileName = "missiles.json";
    private const string CleanMissilesFileName = "missiles_launcher_clean.json";
    private const string CharacterDirectoryName = "character";
    private const string MonstersFileName = "monsters.json";
    private const string CleanMonstersFileName = "monsters_launcher_clean.json";
    private const string LocalDirectoryName = "local";
    private const string LngDirectoryName = "lng";
    private const string StringsDirectoryName = "strings";
    private const string CleanStringsDirectoryName = "strings_launcher_clean";
    private const string LayoutsProfileHdFileName = "_profilehd.json";
    private const string CleanLayoutsProfileHdFileName = "layouts_profilehd_launcher_clean.json";
    private const string CleanArmorTweaksDirectoryName = "armor_launcher_clean";
    private const string TexturesDirectoryName = "textures";
    private const string VignetteDirectoryName = "vignette";
    private const string VignetteFileName = "vignette.texture";
    private const string BundledVignetteAssetPath = "Assets/Vignette/vignette.texture";
    internal const string DisableExtraBloodBackupClaimId = "launcher-tweak:disable-extra-blood";
    internal const string RemoveItemLightBeamsBackupClaimId = "launcher-tweak:remove-item-light-beams";
    private const string CharStatsFileName = "charstats.txt";
    private const string DifficultyLevelsFileName = "DifficultyLevels.txt";
    private const string SkillsFileName = "skills.txt";
    private const string StatesFileName = "states.txt";
    private const string PropertiesFileName = "Properties.txt";
    private const string TreasureClassExFileName = "treasureclassex.txt";
    private const string SoundsFileName = "sounds.txt";
    private const string DesecratedEnterHdSoundKey = "desecrated_enter_hd";
    private const string DesecratedZonesFileName = "desecratedzones.json";
    private const string CleanDesecratedZonesFileName = "desecratedzones_launcher_clean.json";
    private const string EnvDirectoryName = "env";
    private const string VisDirectoryName = "vis";
    private const string DesecratedFilePattern = "desecrated";
    private const string CleanVisDirectoryName = "vis_launcher_clean";
    private const string WeatherDirectoryName = "weather";
    private const string VisEventMapFileName = "vis_event_map.json";
    private const string SfxDirectoryName = "sfx";
    private const string QuestDirectoryName = "quest";
    private const string DesecratedEnterHdFileName = "desecrated_enter_hd.flac";
    private const string GeneratedTweaksFolderName = "mod-tweaks";
    private static readonly string[] ItemLightBeamVfxPaths =
    [
        "data/hd/vfx/particles/overlays/object/horadric_light/fx_horadric_light.particles",
        "data/hd/vfx/particles/overlays/paladin/aura_fanatic/aura_fanatic.particles",
        "data/hd/vfx/particles/overlays/common/valkyriestart/valkriestart_overlay.particles"
    ];
    private static readonly string[] ItemLightBeamRelativePaths =
    [
        "data/hd/items/misc/gem/jewels.json",
        "data/hd/items/misc/grabbers/grabber_tool.json",
        "data/hd/items/misc/key/destruction_key.json",
        "data/hd/items/misc/key/hate_key.json",
        "data/hd/items/misc/key/terror_key.json",
        "data/hd/items/misc/powerorbs/orbofassemblage.json",
        "data/hd/items/misc/powerorbs/orbofassemblage_stacked.json",
        "data/hd/items/misc/powerorbs/orbofconversion.json",
        "data/hd/items/misc/powerorbs/orbofconversion_stacked.json",
        "data/hd/items/misc/powerorbs/orbofcorruption.json",
        "data/hd/items/misc/powerorbs/orbofcorruption_stacked.json",
        "data/hd/items/misc/powerorbs/orbofinfusion.json",
        "data/hd/items/misc/powerorbs/orbofinfusion_stacked.json",
        "data/hd/items/misc/powerorbs/orbofrenewal.json",
        "data/hd/items/misc/powerorbs/orbofrenewal_stacked.json",
        "data/hd/items/misc/powerorbs/orbofshadows.json",
        "data/hd/items/misc/powerorbs/orbofshadows_stacked.json",
        "data/hd/items/misc/powerorbs/orbofsocketing.json",
        "data/hd/items/misc/powerorbs/orbofsocketing_stacked.json",
        "data/hd/items/misc/quest/baal_calamity.json",
        "data/hd/items/misc/quest/diablo_ire.json",
        "data/hd/items/misc/quest/mephisto_animus.json",
        "data/hd/items/misc/rune/lem_rune.json",
        "data/hd/items/misc/rune/lem_rune_stacked.json",
        "data/hd/items/misc/rune/pul_rune.json",
        "data/hd/items/misc/rune/pul_rune_stacked.json",
        "data/hd/items/misc/rune/um_rune.json",
        "data/hd/items/misc/rune/um_rune_stacked.json",
        "data/hd/items/misc/rune/mal_rune.json",
        "data/hd/items/misc/rune/mal_rune_stacked.json",
        "data/hd/items/misc/rune/ist_rune.json",
        "data/hd/items/misc/rune/ist_rune_stacked.json",
        "data/hd/items/misc/rune/gul_rune.json",
        "data/hd/items/misc/rune/gul_rune_stacked.json",
        "data/hd/items/misc/rune/vex_rune.json",
        "data/hd/items/misc/rune/vex_rune_stacked.json",
        "data/hd/items/misc/rune/ohm_rune.json",
        "data/hd/items/misc/rune/ohm_rune_stacked.json",
        "data/hd/items/misc/rune/lo_rune.json",
        "data/hd/items/misc/rune/lo_rune_stacked.json",
        "data/hd/items/misc/rune/sur_rune.json",
        "data/hd/items/misc/rune/sur_rune_stacked.json",
        "data/hd/items/misc/rune/ber_rune.json",
        "data/hd/items/misc/rune/ber_rune_stacked.json",
        "data/hd/items/misc/rune/jah_rune.json",
        "data/hd/items/misc/rune/jah_rune_stacked.json",
        "data/hd/items/misc/rune/cham_rune.json",
        "data/hd/items/misc/rune/cham_rune_stacked.json",
        "data/hd/items/misc/rune/zod_rune.json",
        "data/hd/items/misc/rune/zod_rune_stacked.json",
        "data/hd/items/misc/storage/gem_cluster.json",
        "data/hd/items/misc/storage/storage1.json",
        "data/hd/items/misc/storage/storage2.json"
    ];
    private static readonly string[] DisableExtraBloodRelativePaths =
    [
        "data/hd/character/enemy/arach1/animation/combined.timelines",
        "data/hd/character/enemy/baalminion1/animation/combined.timelines",
        "data/hd/character/enemy/baboon1/animation/combined.timelines",
        "data/hd/character/enemy/baboon6/animation/combined.timelines",
        "data/hd/character/enemy/batdemon1/animation/combined.timelines",
        "data/hd/character/enemy/bighead1/animation/combined.timelines",
        "data/hd/character/enemy/bloodlord1/animation/combined.timelines",
        "data/hd/character/enemy/bloodraven/animation/combined.timelines",
        "data/hd/character/enemy/blunderbore1/animation/combined.timelines",
        "data/hd/character/enemy/brute2/animation/combined.timelines",
        "data/hd/character/enemy/cantor1.json",
        "data/hd/character/enemy/cantor1/animation/combined.timelines",
        "data/hd/character/enemy/cantor1/textures/zakarum_priest_fur_alb.texture",
        "data/hd/character/enemy/cantor1/textures/zakarum_priest_fur_ktint.texture",
        "data/hd/character/enemy/cantor1/textures/zakarum_priest_fur_nrm.texture",
        "data/hd/character/enemy/cantor1/textures/zakarum_priest_fur_orm.texture",
        "data/hd/character/enemy/cantor1/textures/zakarum_priest_hair_alb.texture",
        "data/hd/character/enemy/clawviper1/animation/combined.timelines",
        "data/hd/character/enemy/corruptrogue1.json",
        "data/hd/character/enemy/corruptrogue1/animation/combined.timelines",
        "data/hd/character/enemy/councilmember1/animation/combined.timelines",
        "data/hd/character/enemy/cowking/animation/combined.timelines",
        "data/hd/character/enemy/cr_archer1.json",
        "data/hd/character/enemy/cr_archer1/animation/combined.timelines",
        "data/hd/character/enemy/cr_lancer1.json",
        "data/hd/character/enemy/cr_lancer1/animation/combined.timelines",
        "data/hd/character/enemy/darkelder/animation/combined.timelines",
        "data/hd/character/enemy/deathmauler1/animation/combined.timelines",
        "data/hd/character/enemy/doomknight1.json",
        "data/hd/character/enemy/doomknight1/animation/combined.timelines",
        "data/hd/character/enemy/doomknight2.json",
        "data/hd/character/enemy/doomknight2/animation/combined.timelines",
        "data/hd/character/enemy/doomknight3.json",
        "data/hd/character/enemy/doomknight3/animation/combined.timelines",
        "data/hd/character/enemy/fallen1/animation/combined.timelines",
        "data/hd/character/enemy/fallenshaman1/animation/combined.timelines",
        "data/hd/character/enemy/fetish1/animation/combined.timelines",
        "data/hd/character/enemy/fetishblow1/animation/combined.timelines",
        "data/hd/character/enemy/fetishshaman1/animation/combined.timelines",
        "data/hd/character/enemy/fingermage1/animation/combined.timelines",
        "data/hd/character/enemy/foulcrow1/animation/combined.timelines",
        "data/hd/character/enemy/goatman1/animation/combined.timelines",
        "data/hd/character/enemy/griswold/animation/combined.timelines",
        "data/hd/character/enemy/hellbovine/animation/combined.timelines",
        "data/hd/character/enemy/imp1/animation/combined.timelines",
        "data/hd/character/enemy/imp1/textures/imp_ash_alb.texture",
        "data/hd/character/enemy/imp1/textures/imp_ash_orm.texture",
        "data/hd/character/enemy/maggotbaby1/animation/combined.timelines",
        "data/hd/character/enemy/megademon1/animation/combined.timelines",
        "data/hd/character/enemy/minion1/animation/combined.timelines",
        "data/hd/character/enemy/mosquito1/animation/combined.timelines",
        "data/hd/character/enemy/mummy1.json",
        "data/hd/character/enemy/mummy1/animation/combined.timelines",
        "data/hd/character/enemy/pantherwoman1/animation/combined.timelines",
        "data/hd/character/enemy/putriddefiler1/animation/combined.timelines",
        "data/hd/character/enemy/quillrat1/animation/combined.timelines",
        "data/hd/character/enemy/radament/animation/combined.timelines",
        "data/hd/character/enemy/regurgitator1/animation/combined.timelines",
        "data/hd/character/enemy/sandleaper1/animation/combined.timelines",
        "data/hd/character/enemy/sandmaggot1/animation/combined.timelines",
        "data/hd/character/enemy/sandraider1/animation/combined.timelines",
        "data/hd/character/enemy/scarab1/animation/combined.timelines",
        "data/hd/character/enemy/siegebeast1/animation/combined.timelines",
        "data/hd/character/enemy/slinger1/animation/combined.timelines",
        "data/hd/character/enemy/slinger5/animation/combined.timelines",
        "data/hd/character/enemy/smith/animation/combined.timelines",
        "data/hd/character/enemy/snowyeti1/animation/combined.timelines",
        "data/hd/character/enemy/succubus1/animation/combined.timelines",
        "data/hd/character/enemy/succubus1/textures/succubus_skeleton_alb.texture",
        "data/hd/character/enemy/succubus1/textures/succubusdeath_alb.texture",
        "data/hd/character/enemy/succubuswitch1/animation/combined.timelines",
        "data/hd/character/enemy/succubuswitch1/textures/succubuswitch_death_alb.texture",
        "data/hd/character/enemy/suicideminion1/animation/combined.timelines",
        "data/hd/character/enemy/tentaclehead1/animation/combined.timelines",
        "data/hd/character/enemy/unraveler1/animation/combined.timelines",
        "data/hd/character/enemy/vampire1/animation/combined.timelines",
        "data/hd/character/enemy/vilechild1/animation/combined.timelines",
        "data/hd/character/enemy/vilemother1/animation/combined.timelines",
        "data/hd/character/enemy/vulture1/animation/combined.timelines",
        "data/hd/character/enemy/zealot1/animation/combined.timelines",
        "data/hd/character/enemy/zombie1/animation/combined.timelines",
        "data/hd/global/sfx/monster/baalminion/death1_hd.flac",
        "data/hd/global/sfx/monster/baalminion/death2_hd.flac",
        "data/hd/global/sfx/monster/baalminion/death3_hd.flac",
        "data/hd/global/sfx/monster/baalminion/death4_hd.flac",
        "data/hd/global/sfx/monster/baboondemon/monster_baboondemon_death_1_hd.flac",
        "data/hd/global/sfx/monster/baboondemon/monster_baboondemon_death_2_hd.flac",
        "data/hd/global/sfx/monster/baboondemon/monster_baboondemon_death_3_hd.flac",
        "data/hd/global/sfx/monster/baboondemon/monster_baboondemon_death_4_hd.flac",
        "data/hd/global/sfx/monster/baboondemon/monster_baboondemon_death_5_hd.flac",
        "data/hd/global/sfx/monster/batdemon/monster_batdemon_death_1_hd.flac",
        "data/hd/global/sfx/monster/batdemon/monster_batdemon_death_2_hd.flac",
        "data/hd/global/sfx/monster/batdemon/monster_batdemon_death_3_hd.flac",
        "data/hd/global/sfx/monster/batdemon/monster_batdemon_death_4_hd.flac",
        "data/hd/global/sfx/monster/batdemon/monster_batdemon_death_5_hd.flac",
        "data/hd/global/sfx/monster/beetle/monster_beetle_death_1_hd.flac",
        "data/hd/global/sfx/monster/beetle/monster_beetle_death_2_hd.flac",
        "data/hd/global/sfx/monster/beetle/monster_beetle_death_3_hd.flac",
        "data/hd/global/sfx/monster/beetle/monster_beetle_death_4_hd.flac",
        "data/hd/global/sfx/monster/bighead/monster_bighead_death_1_hd.flac",
        "data/hd/global/sfx/monster/bighead/monster_bighead_death_2_hd.flac",
        "data/hd/global/sfx/monster/bighead/monster_bighead_death_3_hd.flac",
        "data/hd/global/sfx/monster/bighead/monster_bighead_death_4_hd.flac",
        "data/hd/global/sfx/monster/bloodlord/monster_bloodlord_death_1_hd.flac",
        "data/hd/global/sfx/monster/bloodlord/monster_bloodlord_death_2_hd.flac",
        "data/hd/global/sfx/monster/bloodlord/monster_bloodlord_death_3_hd.flac",
        "data/hd/global/sfx/monster/bloodlord/monster_bloodlord_death_4_hd.flac",
        "data/hd/global/sfx/monster/bloodraven/monster_bloodraven_death_hd.flac",
        "data/hd/global/sfx/monster/bloodraven/monster_bloodraven_gethit_1_hd.flac",
        "data/hd/global/sfx/monster/bloodraven/monster_bloodraven_gethit_2_hd.flac",
        "data/hd/global/sfx/monster/bloodraven/monster_bloodraven_gethit_3_hd.flac",
        "data/hd/global/sfx/monster/bloodraven/monster_bloodraven_gethit_4_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_1_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_2_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_3_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_4_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_5_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_attack_6_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_die_1_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_die_2_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_die_3_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_die_4_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_die_5_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_1_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_2_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_3_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_4_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_5_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_gethit_6_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_1_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_2_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_3_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_4_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_5_hd.flac",
        "data/hd/global/sfx/monster/corrupt/monster_corrupt_neutral_6_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_attack_1_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_attack_2_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_attack_3_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_attack_4_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_attack_5_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_death_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_gethit_1_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_gethit_2_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_gethit_3_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_gethit_4_hd.flac",
        "data/hd/global/sfx/monster/countess/monster_countess_gethit_5_hd.flac",
        "data/hd/global/sfx/monster/deathmauler/death1_hd.flac",
        "data/hd/global/sfx/monster/deathmauler/death2_hd.flac",
        "data/hd/global/sfx/monster/deathmauler/death3_hd.flac",
        "data/hd/global/sfx/monster/deathmauler/death4_hd.flac",
        "data/hd/global/sfx/monster/fallen/death10_hd.flac",
        "data/hd/global/sfx/monster/fallen/death11_hd.flac",
        "data/hd/global/sfx/monster/fallen/death12_hd.flac",
        "data/hd/global/sfx/monster/fallen/death13_hd.flac",
        "data/hd/global/sfx/monster/fallen/death14_hd.flac",
        "data/hd/global/sfx/monster/fallen/death2_hd.flac",
        "data/hd/global/sfx/monster/fallen/death3_hd.flac",
        "data/hd/global/sfx/monster/fallen/death4_hd.flac",
        "data/hd/global/sfx/monster/fallen/death5_hd.flac",
        "data/hd/global/sfx/monster/fallen/death6_hd.flac",
        "data/hd/global/sfx/monster/fallen/death7_hd.flac",
        "data/hd/global/sfx/monster/fallen/death8_hd.flac",
        "data/hd/global/sfx/monster/fallen/death9_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death1_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death2_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death3_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death4_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death5_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death6_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death7_hd.flac",
        "data/hd/global/sfx/monster/fallenshaman/death8_hd.flac",
        "data/hd/global/sfx/monster/fingermage/death1_hd.flac",
        "data/hd/global/sfx/monster/fingermage/death2_hd.flac",
        "data/hd/global/sfx/monster/fingermage/death3_hd.flac",
        "data/hd/global/sfx/monster/fingermage/death4_hd.flac",
        "data/hd/global/sfx/monster/frogdemon/monster_frogdemon_death_1_hd.flac",
        "data/hd/global/sfx/monster/frogdemon/monster_frogdemon_death_2_hd.flac",
        "data/hd/global/sfx/monster/frogdemon/monster_frogdemon_death_3_hd.flac",
        "data/hd/global/sfx/monster/frogdemon/monster_frogdemon_death_4_hd.flac",
        "data/hd/global/sfx/monster/frogdemon/monster_frogdemon_death_5_hd.flac",
        "data/hd/global/sfx/monster/goatman/monster_goatman_death_1_hd.flac",
        "data/hd/global/sfx/monster/goatman/monster_goatman_death_2_hd.flac",
        "data/hd/global/sfx/monster/goatman/monster_goatman_death_3_hd.flac",
        "data/hd/global/sfx/monster/goatman/monster_goatman_death_4_hd.flac",
        "data/hd/global/sfx/monster/greatermummy/monster_greatermummy_death_1_hd.flac",
        "data/hd/global/sfx/monster/greatermummy/monster_greatermummy_death_2_hd.flac",
        "data/hd/global/sfx/monster/greatermummy/monster_greatermummy_death_3_hd.flac",
        "data/hd/global/sfx/monster/greatermummy/monster_greatermummy_death_4_hd.flac",
        "data/hd/global/sfx/monster/greatermummy/monster_greatermummy_death_5_hd.flac",
        "data/hd/global/sfx/monster/hawk/monster_bloodhawk_death_1_hd.flac",
        "data/hd/global/sfx/monster/hawk/monster_bloodhawk_death_2_hd.flac",
        "data/hd/global/sfx/monster/hawk/monster_bloodhawk_death_3_hd.flac",
        "data/hd/global/sfx/monster/hawk/monster_bloodhawk_death_4_hd.flac",
        "data/hd/global/sfx/monster/hawknest/monster_hawknest_death_1_hd.flac",
        "data/hd/global/sfx/monster/hawknest/monster_hawknest_death_2_hd.flac",
        "data/hd/global/sfx/monster/hawknest/monster_hawknest_death_3_hd.flac",
        "data/hd/global/sfx/monster/imp/monster_imp_death_1_hd.flac",
        "data/hd/global/sfx/monster/imp/monster_imp_death_2_hd.flac",
        "data/hd/global/sfx/monster/imp/monster_imp_death_3_hd.flac",
        "data/hd/global/sfx/monster/imp/monster_imp_death_4_hd.flac",
        "data/hd/global/sfx/monster/leaper/monster_leaper_death_1_hd.flac",
        "data/hd/global/sfx/monster/leaper/monster_leaper_death_2_hd.flac",
        "data/hd/global/sfx/monster/leaper/monster_leaper_death_3_hd.flac",
        "data/hd/global/sfx/monster/leaper/monster_leaper_death_4_hd.flac",
        "data/hd/global/sfx/monster/leaper/monster_leaper_death_5_hd.flac",
        "data/hd/global/sfx/monster/megademon/death1_hd.flac",
        "data/hd/global/sfx/monster/megademon/death2_hd.flac",
        "data/hd/global/sfx/monster/megademon/death3_hd.flac",
        "data/hd/global/sfx/monster/megademon/death4_hd.flac",
        "data/hd/global/sfx/monster/megademon/death5_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deatha_1_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deatha_2_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deatha_3_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathb_1_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathb_2_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathb_3_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathc_1_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathc_2_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathc_3_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathd_1_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathd_2_hd.flac",
        "data/hd/global/sfx/monster/minion/monster_minion_deathd_3_hd.flac",
        "data/hd/global/sfx/monster/mosquito/death1_hd.flac",
        "data/hd/global/sfx/monster/mosquito/death2_hd.flac",
        "data/hd/global/sfx/monster/mosquito/death3_hd.flac",
        "data/hd/global/sfx/monster/mosquito/death4_hd.flac",
        "data/hd/global/sfx/monster/mummy/monster_mummy_death_1_hd.flac",
        "data/hd/global/sfx/monster/mummy/monster_mummy_death_2_hd.flac",
        "data/hd/global/sfx/monster/mummy/monster_mummy_death_3_hd.flac",
        "data/hd/global/sfx/monster/mummy/monster_mummy_death_4_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_1_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_2_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_3_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_4_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_5_hd.flac",
        "data/hd/global/sfx/monster/overseer/monster_overseer_death_6_hd.flac",
        "data/hd/global/sfx/monster/pantherwoman/monster_pantherwoman_death_1_hd.flac",
        "data/hd/global/sfx/monster/pantherwoman/monster_pantherwoman_death_2_hd.flac",
        "data/hd/global/sfx/monster/pantherwoman/monster_pantherwoman_death_3_hd.flac",
        "data/hd/global/sfx/monster/pantherwoman/monster_pantherwoman_death_4_hd.flac",
        "data/hd/global/sfx/monster/pinhead/monster_pinhead_death_1_hd.flac",
        "data/hd/global/sfx/monster/pinhead/monster_pinhead_death_2_hd.flac",
        "data/hd/global/sfx/monster/pinhead/monster_pinhead_death_3_hd.flac",
        "data/hd/global/sfx/monster/pinhead/monster_pinhead_death_4_hd.flac",
        "data/hd/global/sfx/monster/pinhead/monster_pinhead_death_5_hd.flac",
        "data/hd/global/sfx/monster/putriddefiler/monster_putriddefiler_death_1_hd.flac",
        "data/hd/global/sfx/monster/putriddefiler/monster_putriddefiler_death_2_hd.flac",
        "data/hd/global/sfx/monster/putriddefiler/monster_putriddefiler_death_3_hd.flac",
        "data/hd/global/sfx/monster/putriddefiler/monster_putriddefiler_death_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_1_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_2_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_3_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_5_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_6_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_7_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_8_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_attack_9_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_blowgun_1_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_blowgun_2_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_blowgun_3_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_blowgun_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_1_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_2_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_3_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_5_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_6_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_death_7_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_1_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_2_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_3_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_5_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_6_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_7_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_gethit_8_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_neutral_1_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_neutral_2_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_neutral_3_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_neutral_4_hd.flac",
        "data/hd/global/sfx/monster/pygmy/monster_pygmy_neutral_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_attack_1_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_attack_2_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_attack_3_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_attack_4_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_attack_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_1_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_2_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_3_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_4_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_death_6_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_gethit_1_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_gethit_3_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_gethit_4_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_gethit_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_gethit_6_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_1_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_2_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_3_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_4_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_neutral_6_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_1_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_2_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_3_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_4_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_5_hd.flac",
        "data/hd/global/sfx/monster/pygmyshaman/monster_pygmyshaman_spell_6_hd.flac",
        "data/hd/global/sfx/monster/regurgitator/death1_hd.flac",
        "data/hd/global/sfx/monster/regurgitator/death2_hd.flac",
        "data/hd/global/sfx/monster/regurgitator/death3_hd.flac",
        "data/hd/global/sfx/monster/regurgitator/death4_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_1_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_2_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_3_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_4_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_5_hd.flac",
        "data/hd/global/sfx/monster/sandmaggot/monster_sandmaggot_death_6_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_1_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_2_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_3_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_4_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_5_hd.flac",
        "data/hd/global/sfx/monster/sandmaggotbaby/monster_sandmaggotbaby_death_6_hd.flac",
        "data/hd/global/sfx/monster/sandraider/death1_hd.flac",
        "data/hd/global/sfx/monster/sandraider/death2_hd.flac",
        "data/hd/global/sfx/monster/sandraider/death3_hd.flac",
        "data/hd/global/sfx/monster/sandraider/death4_hd.flac",
        "data/hd/global/sfx/monster/sandraider/death5_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_1_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_2_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_3_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_4_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_5_hd.flac",
        "data/hd/global/sfx/monster/serpentdemon/monster_serpentdemon_death_6_hd.flac",
        "data/hd/global/sfx/monster/siegebeast/monster_siegebeast_death_1_hd.flac",
        "data/hd/global/sfx/monster/siegebeast/monster_siegebeast_death_2_hd.flac",
        "data/hd/global/sfx/monster/siegebeast/monster_siegebeast_death_3_hd.flac",
        "data/hd/global/sfx/monster/siegebeast/monster_siegebeast_death_4_hd.flac",
        "data/hd/global/sfx/monster/siegebeast/monster_siegebeast_death_5_hd.flac",
        "data/hd/global/sfx/monster/smith/monster_smith_death_hd.flac",
        "data/hd/global/sfx/monster/spikefiend/monster_spikefiend_death_1_hd.flac",
        "data/hd/global/sfx/monster/spikefiend/monster_spikefiend_death_2_hd.flac",
        "data/hd/global/sfx/monster/spikefiend/monster_spikefiend_death_3_hd.flac",
        "data/hd/global/sfx/monster/spikefiend/monster_spikefiend_death_4_hd.flac",
        "data/hd/global/sfx/monster/succubus/monster_succubus_death_1_hd.flac",
        "data/hd/global/sfx/monster/succubus/monster_succubus_death_2_hd.flac",
        "data/hd/global/sfx/monster/succubus/monster_succubus_death_3_hd.flac",
        "data/hd/global/sfx/monster/succubus/monster_succubus_death_4_hd.flac",
        "data/hd/global/sfx/monster/tentaclehead/monster_tentaclehead_death_1_hd.flac",
        "data/hd/global/sfx/monster/tentaclehead/monster_tentaclehead_death_2_hd.flac",
        "data/hd/global/sfx/monster/tentaclehead/monster_tentaclehead_death_3_hd.flac",
        "data/hd/global/sfx/monster/tentaclehead/monster_tentaclehead_death_4_hd.flac",
        "data/hd/global/sfx/monster/undeadhorror/death1_hd.flac",
        "data/hd/global/sfx/monster/undeadhorror/death2_hd.flac",
        "data/hd/global/sfx/monster/undeadhorror/death3_hd.flac",
        "data/hd/global/sfx/monster/undeadhorror/death4_hd.flac",
        "data/hd/global/sfx/monster/vampire/monster_vampire_death_1_hd.flac",
        "data/hd/global/sfx/monster/vampire/monster_vampire_death_2_hd.flac",
        "data/hd/global/sfx/monster/vampire/monster_vampire_death_3_hd.flac",
        "data/hd/global/sfx/monster/vampire/monster_vampire_death_4_hd.flac",
        "data/hd/global/sfx/monster/vampire/monster_vampire_death_5_hd.flac",
        "data/hd/global/sfx/monster/vilechild/attack1_hd.flac",
        "data/hd/global/sfx/monster/vilechild/attack2_hd.flac",
        "data/hd/global/sfx/monster/vilechild/attack3_hd.flac",
        "data/hd/global/sfx/monster/vilechild/attack4_hd.flac",
        "data/hd/global/sfx/monster/vilechild/attack5_hd.flac",
        "data/hd/global/sfx/monster/vilechild/death1_hd.flac",
        "data/hd/global/sfx/monster/vilechild/death2_hd.flac",
        "data/hd/global/sfx/monster/vilechild/death3_hd.flac",
        "data/hd/global/sfx/monster/vilechild/death4_hd.flac",
        "data/hd/global/sfx/monster/vilechild/death5_hd.flac",
        "data/hd/global/sfx/monster/vilechild/gethit1_hd.flac",
        "data/hd/global/sfx/monster/vilechild/gethit2_hd.flac",
        "data/hd/global/sfx/monster/vilechild/gethit3_hd.flac",
        "data/hd/global/sfx/monster/vilechild/gethit4_hd.flac",
        "data/hd/global/sfx/monster/vilechild/gethit5_hd.flac",
        "data/hd/global/sfx/monster/vilechild/walk1_hd.flac",
        "data/hd/global/sfx/monster/vilechild/walk2_hd.flac",
        "data/hd/global/sfx/monster/vilechild/walk3_hd.flac",
        "data/hd/global/sfx/monster/vilechild/walk4_hd.flac",
        "data/hd/global/sfx/monster/vilechild/walk5_hd.flac",
        "data/hd/global/sfx/monster/vilemother/attack1_hd.flac",
        "data/hd/global/sfx/monster/vilemother/attack2_hd.flac",
        "data/hd/global/sfx/monster/vilemother/attack3_hd.flac",
        "data/hd/global/sfx/monster/vilemother/attack4_hd.flac",
        "data/hd/global/sfx/monster/vilemother/attack5_hd.flac",
        "data/hd/global/sfx/monster/vilemother/birth1_hd.flac",
        "data/hd/global/sfx/monster/vilemother/birth2_hd.flac",
        "data/hd/global/sfx/monster/vilemother/birth3_hd.flac",
        "data/hd/global/sfx/monster/vilemother/birth4_hd.flac",
        "data/hd/global/sfx/monster/vilemother/death1_hd.flac",
        "data/hd/global/sfx/monster/vilemother/death2_hd.flac",
        "data/hd/global/sfx/monster/vilemother/death3_hd.flac",
        "data/hd/global/sfx/monster/vilemother/death4_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit1_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit2_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit3_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit4_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit5_hd.flac",
        "data/hd/global/sfx/monster/vilemother/gethit6_hd.flac",
        "data/hd/global/sfx/monster/vilemother/neutral1_hd.flac",
        "data/hd/global/sfx/monster/vilemother/neutral2_hd.flac",
        "data/hd/global/sfx/monster/vilemother/neutral3_hd.flac",
        "data/hd/global/sfx/monster/vilemother/neutral4_hd.flac",
        "data/hd/global/sfx/monster/vilemother/neutral5_hd.flac",
        "data/hd/global/sfx/monster/vulturedemon/monster_vulturedemon_death_1_hd.flac",
        "data/hd/global/sfx/monster/vulturedemon/monster_vulturedemon_death_2_hd.flac",
        "data/hd/global/sfx/monster/vulturedemon/monster_vulturedemon_death_3_hd.flac",
        "data/hd/global/sfx/monster/vulturedemon/monster_vulturedemon_death_4_hd.flac",
        "data/hd/global/sfx/monster/vulturedemon/monster_vulturedemon_death_5_hd.flac",
        "data/hd/global/sfx/monster/yeti/monster_yeti_death_1_hd.flac",
        "data/hd/global/sfx/monster/yeti/monster_yeti_death_2_hd.flac",
        "data/hd/global/sfx/monster/yeti/monster_yeti_death_3_hd.flac",
        "data/hd/global/sfx/monster/yeti/monster_yeti_death_4_hd.flac",
        "data/hd/global/sfx/monster/zakarumhighpriest/monster_zakarumhighpriest_death_1_hd.flac",
        "data/hd/global/sfx/monster/zakarumhighpriest/monster_zakarumhighpriest_death_2_hd.flac",
        "data/hd/global/sfx/monster/zakarumhighpriest/monster_zakarumhighpriest_death_3_hd.flac",
        "data/hd/global/sfx/monster/zakarumhighpriest/monster_zakarumhighpriest_death_4_hd.flac",
        "data/hd/global/sfx/monster/zakarumpriest/monster_zakarumpriest_death_1_hd.flac",
        "data/hd/global/sfx/monster/zakarumpriest/monster_zakarumpriest_death_2_hd.flac",
        "data/hd/global/sfx/monster/zakarumpriest/monster_zakarumpriest_death_3_hd.flac",
        "data/hd/global/sfx/monster/zakarumpriest/monster_zakarumpriest_death_4_hd.flac",
        "data/hd/global/sfx/monster/zakarumzealot/monster_zakarumzealot_death_1_hd.flac",
        "data/hd/global/sfx/monster/zakarumzealot/monster_zakarumzealot_death_2_hd.flac",
        "data/hd/global/sfx/monster/zakarumzealot/monster_zakarumzealot_death_3_hd.flac",
        "data/hd/global/sfx/monster/zakarumzealot/monster_zakarumzealot_death_4_hd.flac",
        "data/hd/global/sfx/monster/zombie/monster_zombie_death_1_hd.flac",
        "data/hd/global/sfx/monster/zombie/monster_zombie_death_2_hd.flac",
        "data/hd/global/sfx/monster/zombie/monster_zombie_death_3_hd.flac",
        "data/hd/global/sfx/monster/zombie/monster_zombie_death_4_hd.flac",
        "data/hd/missiles/corpse_explode_guts.json",
        "data/hd/overlays/monster/megademondeath.json",
        "data/hd/overlays/monster/undeadhorrordeath.json",
        "data/hd/vfx/meshes/fx_mesh_bonefrag01_lod0.model",
        "data/hd/vfx/meshes/fx_mesh_bonefrag02_lod0.model",
        "data/hd/vfx/meshes/fx_mesh_bonefrag03_lod0.model",
        "data/hd/vfx/meshes/fx_mesh_bonefrag04_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/bonewall/texture_source/bonewall_props_alb.texture",
        "data/hd/vfx/meshes/lit_mesh/guts/guts01_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts02_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts03_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts04_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts05_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts06_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts07_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts08_lod0.model",
        "data/hd/vfx/meshes/lit_mesh/guts/guts09_lod0.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/gore06_lod0.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/gore06_lod1.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/gore06_lod2.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/gore06_lod3.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/gore06_lod4.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/vfx_mesh_corpse_explosion_gorepile_lod0.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/vfx_mesh_corpse_explosion_gorepile_lod1.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/vfx_mesh_corpse_explosion_gorepile_lod2.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/vfx_mesh_corpse_explosion_gorepile_lod3.model",
        "data/hd/vfx/meshes/missiles/corpse_explosion_gorepile/vfx_mesh_corpse_explosion_gorepile_lod4.model",
        "data/hd/vfx/particles/missiles/corpse_explode_guts/vfx_corpse_explosion.particles",
        "data/hd/vfx/particles/missiles/corpse_explode_guts/vfx_corpse_explosion1.particles",
    ];
    private static readonly string[] HelmetVisualRelativePaths =
    [
        Path.Combine(HelmetDirectoryName, "assault_helmet.json"),
        Path.Combine(HelmetDirectoryName, "avenger_guard.json"),
        Path.Combine(HelmetDirectoryName, "bone_helm.json"),
        Path.Combine(HelmetDirectoryName, "cap_hat.json"),
        Path.Combine(HelmetDirectoryName, "coif_of_glory.json"),
        Path.Combine(HelmetDirectoryName, "colbarbfrenzy_helm.json"),
        Path.Combine(HelmetDirectoryName, "colossal_summons_great_helm.json"),
        Path.Combine(HelmetDirectoryName, "crown.json"),
        Path.Combine(HelmetDirectoryName, "crown_of_thieves.json"),
        Path.Combine(HelmetDirectoryName, "duskdeep.json"),
        Path.Combine(HelmetDirectoryName, "fanged_helm.json"),
        Path.Combine(HelmetDirectoryName, "full_helm.json"),
        Path.Combine(HelmetDirectoryName, "great_helm.json"),
        Path.Combine(HelmetDirectoryName, "helm.json"),
        Path.Combine(HelmetDirectoryName, "horazons_countenance.json"),
        Path.Combine(HelmetDirectoryName, "horned_helm.json"),
        Path.Combine(HelmetDirectoryName, "jawbone_cap.json"),
        Path.Combine(HelmetDirectoryName, "mask.json"),
        Path.Combine(HelmetDirectoryName, "ondals_almighty.json"),
        Path.Combine(HelmetDirectoryName, "rockstopper.json"),
        Path.Combine(HelmetDirectoryName, "skull_cap.json"),
        Path.Combine(HelmetDirectoryName, "unique_warlock_helm.json"),
        Path.Combine(HelmetDirectoryName, "war_bonnet.json"),
        Path.Combine(HelmetDirectoryName, "wormskull.json"),
        Path.Combine(CircletDirectoryName, "circlet.json"),
        Path.Combine(CircletDirectoryName, "coronet.json"),
        Path.Combine(CircletDirectoryName, "diadem.json"),
        Path.Combine(CircletDirectoryName, "tiara.json"),
        Path.Combine(PeltDirectoryName, "antlers.json"),
        Path.Combine(PeltDirectoryName, "falcon_mask.json"),
        Path.Combine(PeltDirectoryName, "hawk_helm.json"),
        Path.Combine(PeltDirectoryName, "spirit_mask.json"),
        Path.Combine(PeltDirectoryName, "wolf_head.json")
    ];

    public static async Task<bool> PrepareForLaunchAsync(IProgress<string>? progress = null)
    {
        ReportProgress(progress, "Resolving mod directories...");
        var excelDirectory = GetExcelDirectory();
        LaunchDiagnostics.Log($"Resolved excel directory: {excelDirectory ?? "<null>"}");
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            Notifications.SendNotification("Excel folder not found in the Reimagined mod directory.", "Warning");
            return false;
        }

        var modRoot = GetMpqBaseDirectory();
        var missilesFilePath = GetMissilesFilePath();
        var monstersFilePath = GetMonstersFilePath();
        var stringsDirectory = GetStringsDirectory();
        var layoutsProfileHdFilePath = GetLayoutsProfileHdFilePath();
        var armorDirectory = GetArmorDirectory();
        var desecratedZonesFilePath = GetDesecratedZonesFilePath();
        var cleanExcelDirectory = GetCleanExcelDirectory(excelDirectory);
        var cleanMissilesFilePath = GetCleanMissilesFilePath(missilesFilePath);
        var cleanMonstersFilePath = GetCleanMonstersFilePath(monstersFilePath);
        var cleanStringsDirectory = GetCleanStringsDirectory(stringsDirectory);
        var cleanLayoutsProfileHdFilePath = GetCleanLayoutsProfileHdFilePath(layoutsProfileHdFilePath);
        var cleanArmorTweaksDirectory = GetCleanArmorTweaksDirectory(armorDirectory);
        var cleanDesecratedZonesFilePath = GetCleanDesecratedZonesFilePath(desecratedZonesFilePath);
        var excelDirectories = GetExcelDirectories(excelDirectory).ToList();
        LaunchDiagnostics.Log($"Resolved missiles file path: {missilesFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved monsters file path: {monstersFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved strings directory path: {stringsDirectory ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved layouts_profilehd.json path: {layoutsProfileHdFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved armor directory path: {armorDirectory ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved desecratedzones.json path: {desecratedZonesFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Excel directories to process: {string.Join(", ", excelDirectories)}");

        try
        {
            // Revert any previous plugin asset writes before tweaks or plugins
            // run so this pass operates on a genuinely pre-plugin baseline.
            // Without this, the next plugin snapshot would capture the prior
            // run's asset as the "original", leaving files behind on disable.
            ReportProgress(progress, "Restoring plugin asset backups...");
            LaunchDiagnostics.Log("Restoring plugin asset backups before tweaks.");
            await PluginAssetBackupService.RestoreAllAsync();

            // Surface asset-copy collisions across enabled plugins exactly once
            // per launch, before any pre-stage entry point or
            // ApplyEnabledPluginsModRootAsync runs. Last-writer-wins semantics
            // are preserved; this only tells the user that a silent override
            // is happening so they can adjust load order if it was unintended.
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                await PluginsService.WarnAssetCollisionsAsync(modRoot, progress);
            }

            ReportProgress(progress, "Preparing clean excel copy...");
            LaunchDiagnostics.Log("Ensuring clean excel copy.");
            await EnsureCleanExcelCopyAsync(excelDirectory, cleanExcelDirectory);
            ReportProgress(progress, "Preparing clean missiles copy...");
            LaunchDiagnostics.Log("Ensuring clean missiles copy.");
            await EnsureCleanMissilesCopyAsync(missilesFilePath, cleanMissilesFilePath);
            ReportProgress(progress, "Preparing clean monsters copy...");
            LaunchDiagnostics.Log("Ensuring clean monsters.json copy.");
            await EnsureCleanMonstersCopyAsync(monstersFilePath, cleanMonstersFilePath);
            ReportProgress(progress, "Preparing clean strings copy...");
            LaunchDiagnostics.Log("Ensuring clean strings directory copy.");
            await EnsureCleanStringsCopyAsync(stringsDirectory, cleanStringsDirectory);
            ReportProgress(progress, "Preparing clean tooltip layout copy...");
            LaunchDiagnostics.Log("Ensuring clean layouts_profilehd.json copy.");
            await EnsureCleanLayoutsProfileHdCopyAsync(layoutsProfileHdFilePath, cleanLayoutsProfileHdFilePath);
            ReportProgress(progress, "Preparing clean helmet visual copy...");
            LaunchDiagnostics.Log("Ensuring clean helmet and circlet JSON copy.");
            await EnsureCleanArmorTweaksCopyAsync(armorDirectory, cleanArmorTweaksDirectory);
            ReportProgress(progress, "Preparing clean desecrated zones copy...");
            LaunchDiagnostics.Log("Ensuring clean desecratedzones.json copy.");
            await EnsureCleanDesecratedZonesCopyAsync(desecratedZonesFilePath, cleanDesecratedZonesFilePath);
            ReportProgress(progress, "Preparing clean vis copy...");
            LaunchDiagnostics.Log("Ensuring clean vis directory copy.");
            await EnsureCleanVisCopyAsync();

            foreach (var targetExcelDirectory in excelDirectories)
            {
                var sourceExcelDirectory = GetCleanVariantDirectory(targetExcelDirectory, excelDirectory, cleanExcelDirectory);
                var targetLabel = Path.GetFileName(targetExcelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                ReportProgress(progress, $"Applying tweaks in {targetLabel}...");
                LaunchDiagnostics.Log($"Processing excel directory. Source={sourceExcelDirectory}, Target={targetExcelDirectory}");
                await ValidateExcelFilesAsync(sourceExcelDirectory);
                await CopyDirectoryAsync(sourceExcelDirectory, targetExcelDirectory, overwrite: true);

                // Pre-stage plugin asset copies whose target lives directly under
                // this excel variant *before* launcher tweaks and parser ops run,
                // so the layering is: clean -> plugin asset -> launcher tweaks ->
                // plugin parser ops. No PluginAssetBackupService snapshot is
                // needed because the launcher's clean-copy step is the recovery
                // mechanism for excel files.
                if (!string.IsNullOrWhiteSpace(modRoot))
                {
                    await PluginsService.ApplyEnabledPluginsExcelAssetsAsync(modRoot, targetExcelDirectory, progress);
                }

                await ApplyTweaksAsync(targetExcelDirectory, progress);

                // Plugin operations targeting excel .txt files (via ParserRegistry)
                // genuinely differ between excel and excel/base because those
                // directories ship distinct .txt content. Mod-root-relative
                // plugin work runs exactly once after this loop.
                await PluginsService.ApplyEnabledPluginsExcelAsync(targetExcelDirectory, progress);
            }

            ReportProgress(progress, "Restoring missiles.json...");
            LaunchDiagnostics.Log("Restoring missiles file from clean copy.");
            await RestoreMissilesFileAsync(cleanMissilesFilePath, missilesFilePath);

            // Pre-stage plugin asset copies targeting missiles.json before
            // launcher tweaks and plugin parser ops run, so launcher tweaks
            // (e.g. RemoveSplashVfx) and plugin parser ops both layer on top.
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                await PluginsService.ApplyEnabledPluginsMissilesAssetsAsync(modRoot, progress);
            }

            ReportProgress(progress, "Applying missiles tweaks...");
            LaunchDiagnostics.Log("Applying missiles tweaks.");
            var profile = MainWindow.Settings.CurrentProfile;
            await ApplyMissilesTweaksAsync(missilesFilePath, profile.RemoveSplashVfx);
            ReportProgress(progress, "Restoring layouts_profilehd.json...");
            LaunchDiagnostics.Log("Restoring tooltip layout file from clean copy.");
            await RestoreLayoutsProfileHdFileAsync(cleanLayoutsProfileHdFilePath, layoutsProfileHdFilePath);
            ReportProgress(progress, "Applying tooltip layout tweaks...");
            LaunchDiagnostics.Log("Applying tooltip layout tweaks.");
            await ApplyTooltipLayoutTweaksAsync(layoutsProfileHdFilePath, profile.MakeTooltipBackgroundOpaque);
            ReportProgress(progress, "Restoring helmet visual files...");
            LaunchDiagnostics.Log("Restoring helmet and circlet visual files from clean copy.");
            await RestoreArmorTweaksAsync(armorDirectory, cleanArmorTweaksDirectory);
            ReportProgress(progress, "Applying helmet visual tweaks...");
            LaunchDiagnostics.Log("Applying helmet visual tweaks.");
            await ApplyHelmetVisualTweaksAsync(armorDirectory, profile.RemoveHelmetVisual);
            ReportProgress(progress, "Restoring desecratedzones.json...");
            LaunchDiagnostics.Log("Restoring desecrated zones file from clean copy.");
            await RestoreDesecratedZonesFileAsync(cleanDesecratedZonesFilePath, desecratedZonesFilePath);
            ReportProgress(progress, "Applying desecrated zones tweaks...");
            LaunchDiagnostics.Log("Applying desecrated zones tweaks.");
            await ApplyDesecratedZonesTweaksAsync(
                desecratedZonesFilePath,
                profile.TerrorizeAllZones,
                profile.ZoneDurationMinutes);
            ReportProgress(progress, "Restoring desecrated vis files...");
            LaunchDiagnostics.Log("Restoring desecrated vis files from clean copy.");
            await RestoreVisFilesAsync();
            ReportProgress(progress, "Applying terror zone purple overlay tweaks...");
            LaunchDiagnostics.Log("Applying terror zone purple overlay tweaks.");
            ApplyTerrorZonePurpleOverlayTweak(profile.TerrorZonePurpleOverlay);
            ReportProgress(progress, "Applying terror zone fanfare tweaks...");
            LaunchDiagnostics.Log("Applying terror zone fanfare tweaks.");
            ApplyTerrorZoneFanfareTweak(profile.RestoreTerrorZoneFanfare);
            ReportProgress(progress, "Applying vignette tweaks...");
            LaunchDiagnostics.Log("Applying vignette tweaks.");
            await ApplyVignetteTweakAsync(profile.RemoveVignette);
            ReportProgress(progress, "Applying extra blood effects tweak...");
            await ApplyDisableExtraBloodTweakAsync(modRoot, profile.DisableExtraBlood);
            ReportProgress(progress, "Applying item light beam tweak...");
            await ApplyRemoveItemLightBeamsTweakAsync(modRoot, profile.RemoveItemLightBeams);

            // Restore monsters.json from the launcher's clean copy and pre-stage
            // any plugin asset copies targeting it. monsters.json has no launcher
            // tweaks today, but the pre-stage runs before plugin parser ops in
            // ApplyEnabledPluginsModRootAsync below so parser ops layer on top.
            ReportProgress(progress, "Restoring monsters.json...");
            LaunchDiagnostics.Log("Restoring monsters file from clean copy.");
            await RestoreMonstersFileAsync(cleanMonstersFilePath, monstersFilePath);
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                await PluginsService.ApplyEnabledPluginsMonstersAssetsAsync(modRoot, progress);
            }

            // Restore the strings directory and pre-stage any plugin asset
            // copies targeting *.json under it, so plugin parser ops in
            // ApplyEnabledPluginsModRootAsync below layer on top.
            ReportProgress(progress, "Restoring strings directory...");
            LaunchDiagnostics.Log("Restoring strings directory from clean copy.");
            await RestoreStringsFromCleanCopyAsync(stringsDirectory, cleanStringsDirectory);
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                await PluginsService.ApplyEnabledPluginsStringsAssetsAsync(modRoot, progress);
            }

            // Apply the remaining mod-root-relative plugin work exactly once per
            // launch: parser ops on missiles/monsters/strings JSON, plugin asset
            // copies whose target is NOT covered by a launcher clean-copy (those
            // were pre-staged earlier and are skipped here), and the
            // animdata.d2 / exanimdata.d2 pair sync.
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                ReportProgress(progress, "Applying plugins...");
                LaunchDiagnostics.Log($"Applying mod-root plugin operations and assets in {modRoot}.");
                await PluginsService.ApplyEnabledPluginsModRootAsync(modRoot, progress);
            }
            else
            {
                LaunchDiagnostics.Log("Skipping mod-root plugin pass: mod root could not be resolved.");
            }

            LaunchDiagnostics.Log("Mod tweak preparation succeeded.");

            return true;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to prepare mod tweaks", ex);
            Notifications.SendNotification($"Failed to prepare mod tweaks: {ex.Message}", "Warning");
            return false;
        }
    }

    private static string? GetMpqBaseDirectory()
    {
        var profile = MainWindow.Settings.CurrentProfile;
        var installDirectory = profile.Type == InstallationType.D2RMM
            ? profile.InstallDirectory
            : InstallDirectoryValidator.NormalizeInstallDirectory(profile.InstallDirectory);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        if (profile.Type == InstallationType.D2RMM)
        {
            return InstallDirectoryValidator.ResolveD2RmmModFolder(installDirectory) ??
                   Path.Combine(installDirectory, ModDirectoryName);
        }

        return Path.Combine(installDirectory, "mods", ModDirectoryName, $"{ModDirectoryName}.mpq");
    }

    private static string? GetExcelDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            "global",
            ExcelDirectoryName);
    }

    private static string? GetMissilesFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            MissilesDirectoryName,
            MissilesFileName);
    }

    private static string? GetLayoutsProfileHdFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            GlobalDirectoryName,
            UiDirectoryName,
            LayoutsDirectoryName,
            LayoutsProfileHdFileName);
    }

    private static string? GetDesecratedZonesFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            ExcelDirectoryName,
            DesecratedZonesFileName);
    }

    private static string? GetArmorDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            ItemsDirectoryName,
            ArmorDirectoryName);
    }

    private static string? GetMonstersFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            CharacterDirectoryName,
            MonstersFileName);
    }

    private static string? GetStringsDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            LocalDirectoryName,
            LngDirectoryName,
            StringsDirectoryName);
    }

    private static string GetCleanExcelDirectory(string excelDirectory)
    {
        var parentDirectory = Directory.GetParent(excelDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new DirectoryNotFoundException("Excel folder parent directory could not be resolved.");
        }

        return Path.Combine(parentDirectory, CleanExcelDirectoryName);
    }

    private static async Task EnsureCleanExcelCopyAsync(string excelDirectory, string cleanExcelDirectory)
    {
        if (Directory.Exists(cleanExcelDirectory))
        {
            return;
        }

        await CopyDirectoryAsync(excelDirectory, cleanExcelDirectory, overwrite: true);
    }

    private static string GetCleanMissilesFilePath(string? missilesFilePath)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json path could not be resolved.");
        }

        var missilesDirectory = Path.GetDirectoryName(missilesFilePath);
        if (string.IsNullOrWhiteSpace(missilesDirectory))
        {
            throw new DirectoryNotFoundException("Missiles folder could not be resolved.");
        }

        return Path.Combine(missilesDirectory, CleanMissilesFileName);
    }

    private static async Task EnsureCleanMissilesCopyAsync(string? missilesFilePath, string cleanMissilesFilePath)
    {
        if (File.Exists(cleanMissilesFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(missilesFilePath) || !File.Exists(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json was not found in the Reimagined hd missiles folder.");
        }

        await CopyFileAsync(missilesFilePath, cleanMissilesFilePath, overwrite: true);
    }

    private static string GetCleanMonstersFilePath(string? monstersFilePath)
    {
        if (string.IsNullOrWhiteSpace(monstersFilePath))
        {
            throw new FileNotFoundException("monsters.json path could not be resolved.");
        }

        var monstersDirectory = Path.GetDirectoryName(monstersFilePath);
        if (string.IsNullOrWhiteSpace(monstersDirectory))
        {
            throw new DirectoryNotFoundException("Character folder could not be resolved.");
        }

        return Path.Combine(monstersDirectory, CleanMonstersFileName);
    }

    private static async Task EnsureCleanMonstersCopyAsync(string? monstersFilePath, string cleanMonstersFilePath)
    {
        if (File.Exists(cleanMonstersFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(monstersFilePath) || !File.Exists(monstersFilePath))
        {
            throw new FileNotFoundException("monsters.json was not found in the Reimagined hd character folder.");
        }

        await CopyFileAsync(monstersFilePath, cleanMonstersFilePath, overwrite: true);
    }

    private static string GetCleanStringsDirectory(string? stringsDirectory)
    {
        if (string.IsNullOrWhiteSpace(stringsDirectory))
        {
            throw new DirectoryNotFoundException("Strings folder could not be resolved.");
        }

        var parentDirectory = Directory.GetParent(stringsDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new DirectoryNotFoundException("Strings folder parent directory could not be resolved.");
        }

        return Path.Combine(parentDirectory, CleanStringsDirectoryName);
    }

    private static async Task EnsureCleanStringsCopyAsync(string? stringsDirectory, string cleanStringsDirectory)
    {
        if (Directory.Exists(cleanStringsDirectory))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stringsDirectory) || !Directory.Exists(stringsDirectory))
        {
            throw new DirectoryNotFoundException("Strings folder was not found in the Reimagined local lng folder.");
        }

        await CopyDirectoryAsync(stringsDirectory, cleanStringsDirectory, overwrite: true);
    }

    private static string GetCleanLayoutsProfileHdFilePath(string? layoutsProfileHdFilePath)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json path could not be resolved.");
        }

        var layoutsDirectory = Path.GetDirectoryName(layoutsProfileHdFilePath);
        if (string.IsNullOrWhiteSpace(layoutsDirectory))
        {
            throw new DirectoryNotFoundException("UI layouts folder could not be resolved.");
        }

        return Path.Combine(layoutsDirectory, CleanLayoutsProfileHdFileName);
    }

    private static async Task EnsureCleanLayoutsProfileHdCopyAsync(string? layoutsProfileHdFilePath, string cleanLayoutsProfileHdFilePath)
    {
        if (File.Exists(cleanLayoutsProfileHdFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath) || !File.Exists(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json was not found in the Reimagined global ui folder.");
        }

        await CopyFileAsync(layoutsProfileHdFilePath, cleanLayoutsProfileHdFilePath, overwrite: true);
    }

    private static string GetCleanArmorTweaksDirectory(string? armorDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder could not be resolved.");
        }

        return Path.Combine(armorDirectory, CleanArmorTweaksDirectoryName);
    }

    private static async Task EnsureCleanArmorTweaksCopyAsync(string? armorDirectory, string cleanArmorTweaksDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (Directory.Exists(cleanArmorTweaksDirectory))
        {
            return;
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var sourceFilePath = Path.Combine(armorDirectory, relativePath);
            var cleanFilePath = Path.Combine(cleanArmorTweaksDirectory, relativePath);
            var cleanDirectory = Path.GetDirectoryName(cleanFilePath);
            if (!string.IsNullOrWhiteSpace(cleanDirectory))
            {
                Directory.CreateDirectory(cleanDirectory);
            }

            if (File.Exists(sourceFilePath))
            {
                await CopyFileAsync(sourceFilePath, cleanFilePath, overwrite: true);
                continue;
            }

            await File.WriteAllTextAsync($"{cleanFilePath}.missing", string.Empty);
        }
    }

    private static string GetCleanDesecratedZonesFilePath(string? desecratedZonesFilePath)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json path could not be resolved.");
        }

        var desecratedZonesDirectory = Path.GetDirectoryName(desecratedZonesFilePath);
        if (string.IsNullOrWhiteSpace(desecratedZonesDirectory))
        {
            throw new DirectoryNotFoundException("Desecrated zones folder could not be resolved.");
        }

        return Path.Combine(desecratedZonesDirectory, CleanDesecratedZonesFileName);
    }

    private static async Task EnsureCleanDesecratedZonesCopyAsync(string? desecratedZonesFilePath, string cleanDesecratedZonesFilePath)
    {
        if (File.Exists(cleanDesecratedZonesFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath) || !File.Exists(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json was not found in the Reimagined hd global excel folder.");
        }

        await CopyFileAsync(desecratedZonesFilePath, cleanDesecratedZonesFilePath, overwrite: true);
    }

    private static async Task RestoreDesecratedZonesFileAsync(string cleanDesecratedZonesFilePath, string? desecratedZonesFilePath)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json path could not be resolved.");
        }

        if (!File.Exists(cleanDesecratedZonesFilePath))
        {
            throw new FileNotFoundException("Clean desecratedzones.json copy was not found.");
        }

        await CopyFileAsync(cleanDesecratedZonesFilePath, desecratedZonesFilePath, overwrite: true);
    }

    private static async Task ApplyDesecratedZonesTweaksAsync(
        string? desecratedZonesFilePath,
        bool terrorizeAllZones,
        int zoneDurationMinutes)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath) || !File.Exists(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json was not found in the Reimagined hd global excel folder.");
        }

        await DesecratedZonesJsonService.ApplyTerrorZoneTweaksAsync(
            desecratedZonesFilePath,
            zoneDurationMinutes);

        if (!terrorizeAllZones)
        {
            return;
        }

        var updatedEntries = await DesecratedZonesJsonService.MergeActAutoZonesAsync(desecratedZonesFilePath);
        if (updatedEntries == 0)
        {
            throw new InvalidDataException("desecratedzones.json did not contain Act Auto zone boundaries to update.");
        }
    }

    private static IEnumerable<string> GetExcelDirectories(string excelDirectory)
    {
        yield return excelDirectory;

        var baseExcelDirectory = Path.Combine(excelDirectory, BaseExcelDirectoryName);
        if (Directory.Exists(baseExcelDirectory))
        {
            yield return baseExcelDirectory;
        }
    }

    private static string GetCleanVariantDirectory(string targetExcelDirectory, string excelDirectory, string cleanExcelDirectory)
    {
        var relativePath = Path.GetRelativePath(excelDirectory, targetExcelDirectory);
        return relativePath == "."
            ? cleanExcelDirectory
            : Path.Combine(cleanExcelDirectory, relativePath);
    }

    private static string GetExcelFilePath(string excelDirectory, string fileName)
    {
        var exactPath = Path.Combine(excelDirectory, fileName);
        if (File.Exists(exactPath))
        {
            return exactPath;
        }

        var actualFileName = Directory
            .EnumerateFiles(excelDirectory)
            .Select(Path.GetFileName)
            .FirstOrDefault(f => f is not null && f.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (actualFileName == null)
        {
            throw new FileNotFoundException($"{fileName} was not found in the Reimagined excel folder: {excelDirectory}");
        }

        return Path.Combine(excelDirectory, actualFileName);
    }

    private static Task ValidateExcelFilesAsync(string excelDirectory)
    {
        _ = GetExcelFilePath(excelDirectory, CharStatsFileName);
        _ = GetExcelFilePath(excelDirectory, DifficultyLevelsFileName);
        _ = GetExcelFilePath(excelDirectory, SkillsFileName);
        _ = GetExcelFilePath(excelDirectory, StatesFileName);
        _ = GetExcelFilePath(excelDirectory, PropertiesFileName);

        return Task.CompletedTask;
    }

    private static async Task ApplyTweaksAsync(string excelDirectory, IProgress<string>? progress)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        ReportProgress(progress, "Updating charstats.txt...");
        LaunchDiagnostics.Log($"Applying charstats tweaks in {excelDirectory}.");
        await ApplyCharStatsTweaksAsync(
            GetExcelFilePath(excelDirectory, CharStatsFileName),
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel);
        ReportProgress(progress, "Updating skills.txt...");
        LaunchDiagnostics.Log($"Applying skills tweaks in {excelDirectory}.");
        await ApplySkillsTweaksAsync(
            GetExcelFilePath(excelDirectory, SkillsFileName),
            profile.MaxSkillLevel,
            profile.RemoveFadeEffect);
        ReportProgress(progress, "Updating DifficultyLevels.txt...");
        LaunchDiagnostics.Log($"Applying difficulty tweaks in {excelDirectory}.");
        await ApplyDifficultyLevelsTweaksAsync(
            GetExcelFilePath(excelDirectory, DifficultyLevelsFileName),
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);
        ReportProgress(progress, "Updating states.txt...");
        LaunchDiagnostics.Log($"Applying states tweaks in {excelDirectory}.");
        await ApplyStatesTweaksAsync(
            GetExcelFilePath(excelDirectory, StatesFileName),
            profile.RemovePaladinAuraSound,
            profile.RemoveFadeEffect);
        ReportProgress(progress, "Updating Properties.txt...");
        LaunchDiagnostics.Log($"Applying properties tweaks in {excelDirectory}.");
        await ApplyPropertiesTweaksAsync(
            GetExcelFilePath(excelDirectory, PropertiesFileName),
            profile.RemoveFadeEffect);
        ReportProgress(progress, "Updating treasureclassex.txt...");
        LaunchDiagnostics.Log($"Applying treasure class tweaks in {excelDirectory}.");
        await ApplyTreasureClassExTweaksAsync(
            GetExcelFilePath(excelDirectory, TreasureClassExFileName),
            profile.OrbStackDrops,
            profile.RuneStackDrops);
        var soundsFilePath = GetExcelFilePath(excelDirectory, SoundsFileName);
        if (File.Exists(soundsFilePath))
        {
            ReportProgress(progress, "Updating sounds.txt...");
            LaunchDiagnostics.Log($"Applying sounds tweaks in {excelDirectory}.");
            await ApplySoundsTweaksAsync(soundsFilePath, profile.RestoreTerrorZoneFanfare);
        }
    }

    private static async Task ApplySoundsTweaksAsync(string soundsFilePath, bool restoreTerrorZoneFanfare)
    {
        var entries = (await SoundsParser.GetEntries(soundsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("sounds.txt did not contain any editable rows.");
        }

        var volume = restoreTerrorZoneFanfare ? 255 : 0;
        var pitch = restoreTerrorZoneFanfare ? 100 : 0;

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.Sounds>(entries.Count);

        foreach (var entry in entries)
        {
            var modified = entry;

            if (string.Equals(modified.Sound, DesecratedEnterHdSoundKey, StringComparison.OrdinalIgnoreCase))
            {
                modified = modified with
                {
                    VolumeMin = volume,
                    VolumeMax = volume,
                    PitchMin = pitch,
                    PitchMax = pitch
                };
                updatedRows++;
            }

            updatedEntries.Add(modified);
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException(
                $"sounds.txt did not contain a '{DesecratedEnterHdSoundKey}' row to update.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            soundsFilePath,
            (rows, filePath, outputDirectory, cancellationToken) =>
                SoundsParser.SaveEntries(rows, filePath, outputDirectory, cancellationToken));
    }

    private static void ReportProgress(IProgress<string>? progress, string message)
    {
        progress?.Report(message);
        LaunchDiagnostics.Log($"STATUS: {message}");
    }

    private static async Task RestoreMissilesFileAsync(string cleanMissilesFilePath, string? missilesFilePath)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json path could not be resolved.");
        }

        if (!File.Exists(cleanMissilesFilePath))
        {
            throw new FileNotFoundException("Clean missiles.json copy was not found.");
        }

        await CopyFileAsync(cleanMissilesFilePath, missilesFilePath, overwrite: true);
    }

    private static async Task RestoreMonstersFileAsync(string cleanMonstersFilePath, string? monstersFilePath)
    {
        if (string.IsNullOrWhiteSpace(monstersFilePath))
        {
            throw new FileNotFoundException("monsters.json path could not be resolved.");
        }

        if (!File.Exists(cleanMonstersFilePath))
        {
            throw new FileNotFoundException("Clean monsters.json copy was not found.");
        }

        await CopyFileAsync(cleanMonstersFilePath, monstersFilePath, overwrite: true);
    }

    private static async Task RestoreStringsFromCleanCopyAsync(string? stringsDirectory, string cleanStringsDirectory)
    {
        if (string.IsNullOrWhiteSpace(stringsDirectory))
        {
            throw new DirectoryNotFoundException("Strings folder could not be resolved.");
        }

        if (!Directory.Exists(cleanStringsDirectory))
        {
            throw new DirectoryNotFoundException("Clean strings copy was not found.");
        }

        await CopyDirectoryAsync(cleanStringsDirectory, stringsDirectory, overwrite: true);
    }

    private static async Task ApplyMissilesTweaksAsync(string? missilesFilePath, bool removeSplashVfx)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath) || !File.Exists(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json was not found in the Reimagined hd missiles folder.");
        }

        if (!removeSplashVfx)
        {
            return;
        }

        var updatedEntries = await MissilesJsonService.ClearProcSplashExplodeAsync(missilesFilePath);
        if (updatedEntries == 0)
        {
            throw new InvalidDataException("missiles.json did not contain a proc_splash_explode entry to update.");
        }
    }

    private static async Task RestoreLayoutsProfileHdFileAsync(string cleanLayoutsProfileHdFilePath, string? layoutsProfileHdFilePath)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json path could not be resolved.");
        }

        if (!File.Exists(cleanLayoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("Clean layouts_profilehd.json copy was not found.");
        }

        await CopyFileAsync(cleanLayoutsProfileHdFilePath, layoutsProfileHdFilePath, overwrite: true);
    }

    private static async Task ApplyTooltipLayoutTweaksAsync(string? layoutsProfileHdFilePath, bool makeTooltipBackgroundOpaque)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath) || !File.Exists(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json was not found in the Reimagined global ui folder.");
        }

        if (!makeTooltipBackgroundOpaque)
        {
            return;
        }

        var updatedValues = await TooltipStyleJsonService.MakeTooltipBackgroundOpaqueAsync(layoutsProfileHdFilePath);
        if (updatedValues == 0)
        {
            throw new InvalidDataException("layouts_profilehd.json did not contain TooltipStyle background colors to update.");
        }
    }

    private static async Task RestoreArmorTweaksAsync(string? armorDirectory, string cleanArmorTweaksDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (!Directory.Exists(cleanArmorTweaksDirectory))
        {
            throw new DirectoryNotFoundException("Clean helmet and circlet JSON copy was not found.");
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var targetFilePath = Path.Combine(armorDirectory, relativePath);
            var cleanFilePath = Path.Combine(cleanArmorTweaksDirectory, relativePath);
            var missingMarkerPath = $"{cleanFilePath}.missing";

            if (File.Exists(cleanFilePath))
            {
                await CopyFileAsync(cleanFilePath, targetFilePath, overwrite: true);
                continue;
            }

            if (File.Exists(missingMarkerPath))
            {
                if (File.Exists(targetFilePath))
                {
                    File.Delete(targetFilePath);
                }

                continue;
            }

            // Neither a clean copy nor a ".missing" marker is present for this
            // entry. This happens when the on-disk armor_launcher_clean folder
            // was created (or partially shipped) by a different version of the
            // launcher / mod than the one that owns the current
            // HelmetVisualRelativePaths list -- typically right after a mod
            // install or update, where the freshly-extracted mod tree includes
            // a stale clean folder that doesn't cover every helmet path the
            // launcher now tweaks. The mod doesn't ship these helmet JSONs as
            // baseline files (the launcher only tweaks them into place), so
            // the safe interpretation is "this file is absent from the mod's
            // baseline", which is exactly the semantics of a ".missing"
            // marker. Treat it as such instead of failing the entire launch,
            // and reseed the marker so subsequent launches don't re-trigger
            // the diagnostic.
            LaunchDiagnostics.Log(
                $"Clean helmet visual state missing for {relativePath}; treating as absent baseline and reseeding marker.");

            var cleanEntryDirectory = Path.GetDirectoryName(missingMarkerPath);
            if (!string.IsNullOrWhiteSpace(cleanEntryDirectory))
            {
                Directory.CreateDirectory(cleanEntryDirectory);
            }

            await File.WriteAllTextAsync(missingMarkerPath, string.Empty);

            if (File.Exists(targetFilePath))
            {
                File.Delete(targetFilePath);
            }
        }
    }

    private static async Task ApplyHelmetVisualTweaksAsync(string? armorDirectory, bool removeHelmetVisual)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (!removeHelmetVisual)
        {
            return;
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var filePath = Path.Combine(armorDirectory, relativePath);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, string.Empty);
        }
    }

    private static async Task ApplyVignetteTweakAsync(bool removeVignette)
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return;
        }

        var vignetteDirectory = Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            TexturesDirectoryName,
            VignetteDirectoryName);
        var vignetteFilePath = Path.Combine(vignetteDirectory, VignetteFileName);

        if (!removeVignette)
        {
            if (File.Exists(vignetteFilePath))
            {
                File.Delete(vignetteFilePath);
            }

            return;
        }

        var bundledVignetteFile = Path.Combine(AppContext.BaseDirectory, BundledVignetteAssetPath);
        if (!File.Exists(bundledVignetteFile))
        {
            throw new FileNotFoundException("Bundled vignette.texture asset was not found.");
        }

        Directory.CreateDirectory(vignetteDirectory);
        await CopyFileAsync(bundledVignetteFile, vignetteFilePath, overwrite: true);
    }

    private static async Task ApplyDisableExtraBloodTweakAsync(string? modRoot, bool disableExtraBlood)
    {
        if (!disableExtraBlood || string.IsNullOrWhiteSpace(modRoot))
        {
            return;
        }

        var removedFileCount = 0;
        foreach (var relativePath in DisableExtraBloodRelativePaths)
        {
            var targetFilePath = ResolveModRelativePath(modRoot, relativePath);
            if (!File.Exists(targetFilePath))
            {
                continue;
            }

            await PluginAssetBackupService.RegisterReplacementAsync(
                DisableExtraBloodBackupClaimId,
                targetFilePath);
            File.Delete(targetFilePath);
            removedFileCount++;
        }

        LaunchDiagnostics.Log($"Disabled extra blood effects by removing {removedFileCount} mod assets.");
    }

    private static async Task ApplyRemoveItemLightBeamsTweakAsync(string? modRoot, bool removeItemLightBeams)
    {
        if (!removeItemLightBeams || string.IsNullOrWhiteSpace(modRoot))
        {
            return;
        }

        var updatedFileCount = 0;
        foreach (var relativePath in ItemLightBeamRelativePaths)
        {
            var targetFilePath = ResolveModRelativePath(modRoot, relativePath);
            if (!File.Exists(targetFilePath))
            {
                continue;
            }

            var json = await File.ReadAllTextAsync(targetFilePath);
            var updatedJson = json;
            foreach (var vfxPath in ItemLightBeamVfxPaths)
            {
                updatedJson = updatedJson.Replace(vfxPath, string.Empty, StringComparison.Ordinal);
            }

            if (updatedJson == json)
            {
                continue;
            }

            await PluginAssetBackupService.RegisterReplacementAsync(
                RemoveItemLightBeamsBackupClaimId,
                targetFilePath);
            await File.WriteAllTextAsync(targetFilePath, updatedJson);
            updatedFileCount++;
        }

        LaunchDiagnostics.Log($"Removed item light beams from {updatedFileCount} mod assets.");
    }

    private static string ResolveModRelativePath(string modRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Mod target path must be relative: {relativePath}");
        }

        var fullModRoot = Path.GetFullPath(modRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTargetPath = Path.GetFullPath(Path.Combine(
            fullModRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var modRootPrefix = fullModRoot + Path.DirectorySeparatorChar;
        if (!fullTargetPath.StartsWith(modRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Mod target path escapes the mod directory: {relativePath}");
        }

        return fullTargetPath;
    }

    private static async Task ApplyCharStatsTweaksAsync(
        string charStatsFilePath,
        int skillPointsPerLevel,
        int attributesPerLevel)
    {
        var entries = (await CharStatsParser.GetEntries(charStatsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("charstats.txt did not contain any editable rows.");
        }

        foreach (var entry in entries)
        {
            entry.StatPerLevel = attributesPerLevel;
            entry.SkillsPerLevel = skillPointsPerLevel;
        }

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            GeneratedTweaksFolderName,
            Guid.NewGuid().ToString("N"));
        var generatedFile = await CharStatsParser.SaveEntries(entries, charStatsFilePath, outputDirectory);
        File.Copy(generatedFile.FullName, charStatsFilePath, overwrite: true);
    }

    private static async Task ApplyDifficultyLevelsTweaksAsync(
        string difficultyLevelsFilePath,
        int normalResistPenalty,
        int nightmareResistPenalty,
        int hellResistPenalty)
    {
        var entries = (await DifficultyLevelParser.GetEntries(difficultyLevelsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain any editable rows.");
        }

        var normalFound = false;
        var nightmareFound = false;
        var hellFound = false;

        foreach (var entry in entries)
        {
            switch (entry.Name)
            {
                case "Normal":
                    entry.ResistPenalty = normalResistPenalty;
                    normalFound = true;
                    break;
                case "Nightmare":
                    entry.ResistPenalty = nightmareResistPenalty;
                    nightmareFound = true;
                    break;
                case "Hell":
                    entry.ResistPenalty = hellResistPenalty;
                    hellFound = true;
                    break;
                default:
                    continue;
            }
        }

        if (!normalFound || !nightmareFound || !hellFound)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain Normal, Nightmare, and Hell rows.");
        }

        await SaveGeneratedEntriesAsync(
            entries,
            difficultyLevelsFilePath,
            (updatedEntries, filePath, outputDirectory, cancellationToken) =>
                DifficultyLevelParser.SaveEntries(updatedEntries, filePath, outputDirectory, cancellationToken));
    }

    private static async Task ApplySkillsTweaksAsync(string skillsFilePath, int maxSkillLevel, bool removeFadeEffect)
    {
        var entries = (await SkillsParser.GetEntries(skillsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any editable rows.");
        }

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.Skills>(entries.Count);

        foreach (var entry in entries)
        {
            var modified = entry;

            if (!string.IsNullOrWhiteSpace(modified.CharClass) &&
                int.TryParse(modified.MaxLvl, out var currentMaxLevel) &&
                currentMaxLevel > 0)
            {
                modified = modified with { MaxLvl = maxSkillLevel.ToString() };
                updatedRows++;
            }

            if (removeFadeEffect
                && string.Equals(modified.Skill, "Fade", StringComparison.OrdinalIgnoreCase))
            {
                modified = modified with { PassiveStat1 = string.Empty, PassiveCalc1 = string.Empty };
                updatedRows++;
            }

            updatedEntries.Add(modified);
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any matching rows to update.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            skillsFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                SkillsParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static async Task ApplyStatesTweaksAsync(string statesFilePath, bool removePaladinAuraSound, bool removeFadeEffect)
    {
        if (!removePaladinAuraSound && !removeFadeEffect)
        {
            return;
        }

        var entries = (await StatesParser.GetEntries(statesFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("states.txt did not contain any editable rows.");
        }

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.States>(entries.Count);

        foreach (var entry in entries)
        {
            var modified = entry;

            if (removePaladinAuraSound
                && !string.IsNullOrWhiteSpace(modified.OnSound)
                && (modified.OnSound.StartsWith("paladin_aura_", StringComparison.OrdinalIgnoreCase)
                    || modified.OnSound.StartsWith("paladin_redeemed_soul", StringComparison.OrdinalIgnoreCase)))
            {
                modified = modified with { OnSound = string.Empty };
                updatedRows++;
            }

            if (removeFadeEffect
                && string.Equals(modified.StateId, "fade", StringComparison.OrdinalIgnoreCase))
            {
                modified = modified with { Overlay1 = string.Empty };
                updatedRows++;
            }

            updatedEntries.Add(modified);
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("states.txt did not contain any matching rows to update.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            statesFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                StatesParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static async Task ApplyPropertiesTweaksAsync(string propertiesFilePath, bool removeFadeEffect)
    {
        if (!removeFadeEffect)
        {
            return;
        }

        var entries = (await PropertiesParser.GetEntries(propertiesFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("Properties.txt did not contain any editable rows.");
        }

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.Property>(entries.Count);

        foreach (var entry in entries)
        {
            var modified = entry;

            if (string.Equals(modified.Code, "fade", StringComparison.OrdinalIgnoreCase))
            {
                modified = modified with { Stat1 = string.Empty };
                updatedRows++;
            }

            updatedEntries.Add(modified);
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("Properties.txt did not contain a fade row to update.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            propertiesFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                PropertiesParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private const string GoldReplacement = "Gold 1x";

    private static readonly (string Unstacked, string Stacked)[] OrbReplacementPairs =
    [
        ("ooi", "1oi"),
        ("ooa", "1oa"),
        ("ooc", "1oc"),
        ("ka3", "1ka"),
        ("oos", "1os"),
        ("ooe", "1oe"),
        ("oor", "1or")
    ];

    private static async Task ApplyTreasureClassExTweaksAsync(
        string treasureClassExFilePath,
        StackDropOption orbStackDrops,
        StackDropOption runeStackDrops)
    {
        if (orbStackDrops == StackDropOption.Default && runeStackDrops == StackDropOption.Default)
        {
            return;
        }

        if (!File.Exists(treasureClassExFilePath))
        {
            return;
        }

        var entries = (await TreasureClassParser.GetEntries(treasureClassExFilePath)).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var updatedEntries = new List<D2RReimaginedTools.Models.TreasureClass>(entries.Count);

        foreach (var entry in entries)
        {
            var updated = entry with
            {
                Item1 = ReplaceDropCode(entry.Item1, orbStackDrops, runeStackDrops),
                Item2 = ReplaceDropCode(entry.Item2, orbStackDrops, runeStackDrops),
                Item3 = ReplaceDropCode(entry.Item3, orbStackDrops, runeStackDrops),
                Item4 = ReplaceDropCode(entry.Item4, orbStackDrops, runeStackDrops),
                Item5 = ReplaceDropCode(entry.Item5, orbStackDrops, runeStackDrops),
                Item6 = ReplaceDropCode(entry.Item6, orbStackDrops, runeStackDrops),
                Item7 = ReplaceDropCode(entry.Item7, orbStackDrops, runeStackDrops),
                Item8 = ReplaceDropCode(entry.Item8, orbStackDrops, runeStackDrops),
                Item9 = ReplaceDropCode(entry.Item9, orbStackDrops, runeStackDrops),
                Item10 = ReplaceDropCode(entry.Item10, orbStackDrops, runeStackDrops)
            };
            updatedEntries.Add(updated);
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            treasureClassExFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                TreasureClassParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static string? ReplaceDropCode(
        string? value,
        StackDropOption orbStackDrops,
        StackDropOption runeStackDrops)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (orbStackDrops != StackDropOption.Default)
        {
            foreach (var (unstacked, stacked) in OrbReplacementPairs)
            {
                if (orbStackDrops == StackDropOption.Disabled)
                {
                    if (string.Equals(value, unstacked, StringComparison.Ordinal)
                        || string.Equals(value, stacked, StringComparison.Ordinal))
                    {
                        return GoldReplacement;
                    }
                }
                else
                {
                    var from = orbStackDrops == StackDropOption.Unstacked ? stacked : unstacked;
                    var to = orbStackDrops == StackDropOption.Unstacked ? unstacked : stacked;

                    if (string.Equals(value, from, StringComparison.Ordinal))
                    {
                        return to;
                    }
                }
            }
        }

        if (runeStackDrops != StackDropOption.Default && value.Length == 3)
        {
            var prefix = value[0];
            var suffix = value.Substring(1);

            if (runeStackDrops == StackDropOption.Disabled)
            {
                if ((prefix == 'r' || prefix == 's')
                    && int.TryParse(suffix, out _))
                {
                    return GoldReplacement;
                }
            }
            else
            {
                if (runeStackDrops == StackDropOption.Unstacked
                    && prefix == 's'
                    && int.TryParse(suffix, out _))
                {
                    return "r" + suffix;
                }

                if (runeStackDrops == StackDropOption.Stacked
                    && prefix == 'r'
                    && int.TryParse(suffix, out _))
                {
                    return "s" + suffix;
                }
            }
        }

        return value;
    }

    private static async Task SaveGeneratedEntriesAsync<TEntry>(
        IList<TEntry> entries,
        string sourceFilePath,
        Func<IList<TEntry>, string, string, CancellationToken, Task<FileInfo>> saveEntriesAsync)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            GeneratedTweaksFolderName,
            Guid.NewGuid().ToString("N"));
        var generatedFile = await saveEntriesAsync(entries, sourceFilePath, outputDirectory, CancellationToken.None);
        File.Copy(generatedFile.FullName, sourceFilePath, overwrite: true);
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationFilePath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationFilePath);

            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            await using var sourceStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destinationStream = File.Open(
                destinationFilePath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    private static string? GetVisDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            EnvDirectoryName,
            VisDirectoryName);
    }

    private static string GetCleanVisDirectory(string visDirectory)
    {
        var parentDirectory = Directory.GetParent(visDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new DirectoryNotFoundException("Vis folder parent directory could not be resolved.");
        }

        return Path.Combine(parentDirectory, CleanVisDirectoryName);
    }

    private static async Task EnsureCleanVisCopyAsync()
    {
        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found, skipping clean copy: {visDirectory ?? "<null>"}");
            return;
        }

        var cleanVisDirectory = GetCleanVisDirectory(visDirectory);
        if (Directory.Exists(cleanVisDirectory))
        {
            return;
        }

        var desecratedFiles = Directory.GetFiles(visDirectory, "*.json")
            .Where(f => Path.GetFileName(f).Contains(DesecratedFilePattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (desecratedFiles.Count == 0)
        {
            LaunchDiagnostics.Log("No desecrated vis files found to back up.");
            return;
        }

        Directory.CreateDirectory(cleanVisDirectory);

        foreach (var file in desecratedFiles)
        {
            var fileName = Path.GetFileName(file);
            var cleanFilePath = Path.Combine(cleanVisDirectory, fileName);
            await CopyFileAsync(file, cleanFilePath, overwrite: true);
            LaunchDiagnostics.Log($"Backed up desecrated vis file: {fileName}");
        }
    }

    private static async Task RestoreVisFilesAsync()
    {
        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found, skipping restore: {visDirectory ?? "<null>"}");
            return;
        }

        var cleanVisDirectory = GetCleanVisDirectory(visDirectory);
        if (!Directory.Exists(cleanVisDirectory))
        {
            LaunchDiagnostics.Log("Clean vis directory not found, skipping restore.");
            return;
        }

        foreach (var cleanFile in Directory.GetFiles(cleanVisDirectory, "*.json"))
        {
            var fileName = Path.GetFileName(cleanFile);
            var targetFilePath = Path.Combine(visDirectory, fileName);
            await CopyFileAsync(cleanFile, targetFilePath, overwrite: true);
            LaunchDiagnostics.Log($"Restored desecrated vis file: {fileName}");
        }
    }

    private static string? GetVisEventMapFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            EnvDirectoryName,
            WeatherDirectoryName,
            VisEventMapFileName);
    }

    private static void ApplyTerrorZonePurpleOverlayTweak(bool terrorZonePurpleOverlay)
    {
        if (!terrorZonePurpleOverlay)
        {
            return;
        }

        var visEventMapFilePath = GetVisEventMapFilePath();
        if (!string.IsNullOrWhiteSpace(visEventMapFilePath) && File.Exists(visEventMapFilePath))
        {
            File.Delete(visEventMapFilePath);
            LaunchDiagnostics.Log($"Deleted vis event map file: {visEventMapFilePath}");
        }

        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found: {visDirectory ?? "<null>"}");
            return;
        }

        var files = Directory.GetFiles(visDirectory, "*.json")
            .Where(f => Path.GetFileName(f).Contains(DesecratedFilePattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        LaunchDiagnostics.Log($"Found {files.Count} desecrated vis file(s) to delete.");

        foreach (var file in files)
        {
            File.Delete(file);
            LaunchDiagnostics.Log($"Deleted desecrated vis file: {file}");
        }
    }

    private static string? GetTerrorZoneFanfareFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            SfxDirectoryName,
            QuestDirectoryName,
            DesecratedEnterHdFileName);
    }

    private static void ApplyTerrorZoneFanfareTweak(bool restoreTerrorZoneFanfare)
    {
        if (restoreTerrorZoneFanfare)
        {
            return;
        }

        var fanfareFilePath = GetTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(fanfareFilePath) || !File.Exists(fanfareFilePath))
        {
            LaunchDiagnostics.Log($"Terror zone fanfare file not found: {fanfareFilePath ?? "<null>"}");
            return;
        }

        File.Delete(fanfareFilePath);
        LaunchDiagnostics.Log($"Deleted terror zone fanfare file: {fanfareFilePath}");
    }

    private static async Task CopyFileAsync(string sourceFilePath, string destinationFilePath, bool overwrite)
    {
        var destinationFolder = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(destinationFolder))
        {
            Directory.CreateDirectory(destinationFolder);
        }

        await using var sourceStream = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destinationStream = File.Open(
            destinationFilePath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await sourceStream.CopyToAsync(destinationStream);
    }
}
