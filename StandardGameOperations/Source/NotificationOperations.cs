using System.Text;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public enum GameNotificationColor
{
    Default = -1,
    PureWhite,
    White,
    Black,
    Gray,
    GrayLight,
    GrayDark,
    Red,
    RedLight,
    RedDark,
    Blue,
    BlueLight,
    BlueDark,
    Yellow,
    YellowLight,
    YellowDark,
    Orange,
    OrangeLight,
    OrangeDark,
    Green,
    GreenLight,
    GreenDark,
    Purple,
    PurpleLight,
    PurpleDark,
    Pink,
    RadarHealth,
    RadarArmor,
    RadarDamage,
    NetPlayer1,
    NetPlayer2,
    NetPlayer3,
    NetPlayer4,
    NetPlayer5,
    NetPlayer6,
    NetPlayer7,
    NetPlayer8,
    NetPlayer9,
    NetPlayer10,
    NetPlayer11,
    NetPlayer12,
    NetPlayer13,
    NetPlayer14,
    NetPlayer15,
    NetPlayer16,
    NetPlayer17,
    NetPlayer18,
    NetPlayer19,
    NetPlayer20,
    NetPlayer21,
    NetPlayer22,
    NetPlayer23,
    NetPlayer24,
    NetPlayer25,
    NetPlayer26,
    NetPlayer27,
    NetPlayer28,
    NetPlayer29,
    NetPlayer30,
    NetPlayer31,
    NetPlayer32,
    SimpleBlipDefault,
    MenuBlue,
    MenuGrayLight,
    MenuBlueExtraDark,
    MenuYellow,
    MenuYellowDark,
    MenuGreen,
    MenuGray,
    MenuGrayDark,
    MenuHighlight,
    MenuStandard,
    MenuDimmed,
    MenuExtraDimmed,
    BriefTitle,
    MidGrayMP,
    NetPlayer1Dark,
    NetPlayer2Dark,
    NetPlayer3Dark,
    NetPlayer4Dark,
    NetPlayer5Dark,
    NetPlayer6Dark,
    NetPlayer7Dark,
    NetPlayer8Dark,
    NetPlayer9Dark,
    NetPlayer10Dark,
    NetPlayer11Dark,
    NetPlayer12Dark,
    NetPlayer13Dark,
    NetPlayer14Dark,
    NetPlayer15Dark,
    NetPlayer16Dark,
    NetPlayer17Dark,
    NetPlayer18Dark,
    NetPlayer19Dark,
    NetPlayer20Dark,
    NetPlayer21Dark,
    NetPlayer22Dark,
    NetPlayer23Dark,
    NetPlayer24Dark,
    NetPlayer25Dark,
    NetPlayer26Dark,
    NetPlayer27Dark,
    NetPlayer28Dark,
    NetPlayer29Dark,
    NetPlayer30Dark,
    NetPlayer31Dark,
    NetPlayer32Dark,
    Bronze,
    Silver,
    Gold,
    Platinum,
    Gang1,
    Gang2,
    Gang3,
    Gang4,
    SameCrew,
    Freemode,
    PauseBG,
    Friendly,
    Enemy,
    Location,
    Pickup,
    PauseSingleplayer,
    FreemodeDark,
    InactiveMission,
    Damage,
    PinkLight,
    PMMitemHighlight,
    ScriptVariable,
    Yoga,
    Tennis,
    Golf,
    ShootingRange,
    FlightSchool,
    NorthBlue,
    SocialClub,
    PlatformBlue,
    PlatformGreen,
    PlatformGray,
    FacebookBlue,
    IngameBG,
    Darts,
    Waypoint,
    Michael,
    Franklin,
    Trevor,
    GolfP1,
    GolfP2,
    GolfP3,
    GolfP4,
    WaypointLight,
    WaypointDark,
    PanelLight,
    MichaelDark,
    FranklinDark,
    TrevorDark,
    ObjectiveRoute,
    PauseMapTint,
    PauseDeselect,
    PMWeaponsPurchasable,
    PMWeaponsLocked,
    EndScreenBG,
    Chop,
    PauseMapTintHalf,
    NorthBlueOfficial,
    ScriptVariable2,
    H,
    HDark,
    T,
    TDark,
    HShard,
    ControllerMichael,
    ControllerFranklin,
    ControllerTrevor,
    ControllerChop,
    VideoEditorVideo,
    VideoEditorAudio,
    VideoEditorText,
    HBBlue,
    HBYellow,
    VideoEditorScore,
    VideoEditorAudioFadeout,
    VideoEditorTextFadeout,
    VideoEditorScoreFadeout,
    HeistBackground,
    VideoEditorAmbient,
    VideoEditorAmbientFadeout,
    VideoEditorAmbientDark,
    VideoEditorAmbientLight,
    VideoEditorAmbientMid,
    LowFlow,
    LowFlowDark,
    G1,
    G2,
    G3,
    G4,
    G5,
    G6,
    G7,
    G8,
    G9,
    G10,
    G11,
    G12,
    G13,
    G14,
    G15,
    Adversary,
    DegenRed,
    DegenYellow,
    DegenGreen,
    DegenCyan,
    DegenBlue,
    DegenMagenta,
    Stunt1,
    Stunt2,
    SpecialRaceSeries,
    SpecialRaceSeriesDark,
    CS,
    CSDark,
    TechGreen,
    TechGreenDark,
    TechRed,
    TechGreenVeryDark,
    Placeholder01,
    Placeholder02,
    Placeholder03,
    Placeholder04,
    Placeholder05,
    Placeholder06,
    Placeholder07,
    Placeholder08,
    Placeholder09,
    Placeholder10,
    JunkEnergy
}

public interface INotificationOperations
{
    int Post(
        string message,
        GameNotificationColor color = GameNotificationColor.Default,
        bool blink = false);

    int Post(
        string message,
        GameNotificationColor color,
        bool blink,
        bool showInBrief,
        bool forced);
}

internal sealed class NotificationOperations : INotificationOperations
{
    private const int MaximumTextComponentUtf8Bytes = 99;

    public int Post(
        string message,
        GameNotificationColor color = GameNotificationColor.Default,
        bool blink = false) =>
        Post(message, color, blink, showInBrief: false, forced: false);

    public int Post(
        string message,
        GameNotificationColor color,
        bool blink,
        bool showInBrief,
        bool forced)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length == 0)
        {
            return -1;
        }

        StandardNatives.BEGIN_TEXT_COMMAND_THEFEED_POST("CELL_EMAIL_BCON");
        foreach (string component in SplitUtf8(message))
        {
            StandardNatives.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME(component);
        }
        if (color is not GameNotificationColor.Default)
        {
            StandardNatives.THEFEED_SET_BACKGROUND_COLOR_FOR_NEXT_POST(
                (int)color);
        }
        return forced
            ? StandardNatives.END_TEXT_COMMAND_THEFEED_POST_TICKER_FORCED(
                blink,
                showInBrief)
            : StandardNatives.END_TEXT_COMMAND_THEFEED_POST_TICKER(
                blink,
                showInBrief);
    }

    private static List<string> SplitUtf8(string value)
    {
        List<string> result = [];
        StringBuilder builder = new();
        int byteCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            string text = rune.ToString();
            int runeBytes = Encoding.UTF8.GetByteCount(text);
            if (builder.Length != 0 &&
                byteCount + runeBytes > MaximumTextComponentUtf8Bytes)
            {
                result.Add(builder.ToString());
                builder.Clear();
                byteCount = 0;
            }
            builder.Append(text);
            byteCount += runeBytes;
        }
        if (builder.Length != 0)
        {
            result.Add(builder.ToString());
        }
        return result;
    }
}