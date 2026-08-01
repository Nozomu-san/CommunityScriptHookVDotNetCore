namespace ScriptHookInput.Source

open System
open System.Collections.Generic
open System.Runtime.CompilerServices

[<RequireQualifiedAccess>]
type GameControlId =
    | INPUT_NEXT_CAMERA = 0
    | INPUT_LOOK_LR = 1
    | INPUT_LOOK_UD = 2
    | INPUT_LOOK_UP_ONLY = 3
    | INPUT_LOOK_DOWN_ONLY = 4
    | INPUT_LOOK_LEFT_ONLY = 5
    | INPUT_LOOK_RIGHT_ONLY = 6
    | INPUT_CINEMATIC_SLOWMO = 7
    | INPUT_SCRIPTED_FLY_UD = 8
    | INPUT_SCRIPTED_FLY_LR = 9
    | INPUT_SCRIPTED_FLY_ZUP = 10
    | INPUT_SCRIPTED_FLY_ZDOWN = 11
    | INPUT_WEAPON_WHEEL_UD = 12
    | INPUT_WEAPON_WHEEL_LR = 13
    | INPUT_WEAPON_WHEEL_NEXT = 14
    | INPUT_WEAPON_WHEEL_PREV = 15
    | INPUT_SELECT_NEXT_WEAPON = 16
    | INPUT_SELECT_PREV_WEAPON = 17
    | INPUT_SKIP_CUTSCENE = 18
    | INPUT_CHARACTER_WHEEL = 19
    | INPUT_MULTIPLAYER_INFO = 20
    | INPUT_SPRINT = 21
    | INPUT_JUMP = 22
    | INPUT_ENTER = 23
    | INPUT_ATTACK = 24
    | INPUT_AIM = 25
    | INPUT_LOOK_BEHIND = 26
    | INPUT_PHONE = 27
    | INPUT_SPECIAL_ABILITY = 28
    | INPUT_SPECIAL_ABILITY_SECONDARY = 29
    | INPUT_MOVE_LR = 30
    | INPUT_MOVE_UD = 31
    | INPUT_MOVE_UP_ONLY = 32
    | INPUT_MOVE_DOWN_ONLY = 33
    | INPUT_MOVE_LEFT_ONLY = 34
    | INPUT_MOVE_RIGHT_ONLY = 35
    | INPUT_DUCK = 36
    | INPUT_SELECT_WEAPON = 37
    | INPUT_PICKUP = 38
    | INPUT_SNIPER_ZOOM = 39
    | INPUT_SNIPER_ZOOM_IN_ONLY = 40
    | INPUT_SNIPER_ZOOM_OUT_ONLY = 41
    | INPUT_SNIPER_ZOOM_IN_SECONDARY = 42
    | INPUT_SNIPER_ZOOM_OUT_SECONDARY = 43
    | INPUT_COVER = 44
    | INPUT_RELOAD = 45
    | INPUT_TALK = 46
    | INPUT_DETONATE = 47
    | INPUT_HUD_SPECIAL = 48
    | INPUT_ARREST = 49
    | INPUT_ACCURATE_AIM = 50
    | INPUT_CONTEXT = 51
    | INPUT_CONTEXT_SECONDARY = 52
    | INPUT_WEAPON_SPECIAL = 53
    | INPUT_WEAPON_SPECIAL_TWO = 54
    | INPUT_DIVE = 55
    | INPUT_DROP_WEAPON = 56
    | INPUT_DROP_AMMO = 57
    | INPUT_THROW_GRENADE = 58
    | INPUT_VEH_MOVE_LR = 59
    | INPUT_VEH_MOVE_UD = 60
    | INPUT_VEH_MOVE_UP_ONLY = 61
    | INPUT_VEH_MOVE_DOWN_ONLY = 62
    | INPUT_VEH_MOVE_LEFT_ONLY = 63
    | INPUT_VEH_MOVE_RIGHT_ONLY = 64
    | INPUT_VEH_SPECIAL = 65
    | INPUT_VEH_GUN_LR = 66
    | INPUT_VEH_GUN_UD = 67
    | INPUT_VEH_AIM = 68
    | INPUT_VEH_ATTACK = 69
    | INPUT_VEH_ATTACK2 = 70
    | INPUT_VEH_ACCELERATE = 71
    | INPUT_VEH_BRAKE = 72
    | INPUT_VEH_DUCK = 73
    | INPUT_VEH_HEADLIGHT = 74
    | INPUT_VEH_EXIT = 75
    | INPUT_VEH_HANDBRAKE = 76
    | INPUT_VEH_HOTWIRE_LEFT = 77
    | INPUT_VEH_HOTWIRE_RIGHT = 78
    | INPUT_VEH_LOOK_BEHIND = 79
    | INPUT_VEH_CIN_CAM = 80
    | INPUT_VEH_NEXT_RADIO = 81
    | INPUT_VEH_PREV_RADIO = 82
    | INPUT_VEH_NEXT_RADIO_TRACK = 83
    | INPUT_VEH_PREV_RADIO_TRACK = 84
    | INPUT_VEH_RADIO_WHEEL = 85
    | INPUT_VEH_HORN = 86
    | INPUT_VEH_FLY_THROTTLE_UP = 87
    | INPUT_VEH_FLY_THROTTLE_DOWN = 88
    | INPUT_VEH_FLY_YAW_LEFT = 89
    | INPUT_VEH_FLY_YAW_RIGHT = 90
    | INPUT_VEH_PASSENGER_AIM = 91
    | INPUT_VEH_PASSENGER_ATTACK = 92
    | INPUT_VEH_SPECIAL_ABILITY_FRANKLIN = 93
    | INPUT_VEH_STUNT_UD = 94
    | INPUT_VEH_CINEMATIC_UD = 95
    | INPUT_VEH_CINEMATIC_UP_ONLY = 96
    | INPUT_VEH_CINEMATIC_DOWN_ONLY = 97
    | INPUT_VEH_CINEMATIC_LR = 98
    | INPUT_VEH_SELECT_NEXT_WEAPON = 99
    | INPUT_VEH_SELECT_PREV_WEAPON = 100
    | INPUT_VEH_ROOF = 101
    | INPUT_VEH_JUMP = 102
    | INPUT_VEH_GRAPPLING_HOOK = 103
    | INPUT_VEH_SHUFFLE = 104
    | INPUT_VEH_DROP_PROJECTILE = 105
    | INPUT_VEH_MOUSE_CONTROL_OVERRIDE = 106
    | INPUT_VEH_FLY_ROLL_LR = 107
    | INPUT_VEH_FLY_ROLL_LEFT_ONLY = 108
    | INPUT_VEH_FLY_ROLL_RIGHT_ONLY = 109
    | INPUT_VEH_FLY_PITCH_UD = 110
    | INPUT_VEH_FLY_PITCH_UP_ONLY = 111
    | INPUT_VEH_FLY_PITCH_DOWN_ONLY = 112
    | INPUT_VEH_FLY_UNDERCARRIAGE = 113
    | INPUT_VEH_FLY_ATTACK = 114
    | INPUT_VEH_FLY_SELECT_NEXT_WEAPON = 115
    | INPUT_VEH_FLY_SELECT_PREV_WEAPON = 116
    | INPUT_VEH_FLY_SELECT_TARGET_LEFT = 117
    | INPUT_VEH_FLY_SELECT_TARGET_RIGHT = 118
    | INPUT_VEH_FLY_VERTICAL_FLIGHT_MODE = 119
    | INPUT_VEH_FLY_DUCK = 120
    | INPUT_VEH_FLY_ATTACK_CAMERA = 121
    | INPUT_VEH_FLY_MOUSE_CONTROL_OVERRIDE = 122
    | INPUT_VEH_SUB_TURN_LR = 123
    | INPUT_VEH_SUB_TURN_LEFT_ONLY = 124
    | INPUT_VEH_SUB_TURN_RIGHT_ONLY = 125
    | INPUT_VEH_SUB_PITCH_UD = 126
    | INPUT_VEH_SUB_PITCH_UP_ONLY = 127
    | INPUT_VEH_SUB_PITCH_DOWN_ONLY = 128
    | INPUT_VEH_SUB_THROTTLE_UP = 129
    | INPUT_VEH_SUB_THROTTLE_DOWN = 130
    | INPUT_VEH_SUB_ASCEND = 131
    | INPUT_VEH_SUB_DESCEND = 132
    | INPUT_VEH_SUB_TURN_HARD_LEFT = 133
    | INPUT_VEH_SUB_TURN_HARD_RIGHT = 134
    | INPUT_VEH_SUB_MOUSE_CONTROL_OVERRIDE = 135
    | INPUT_VEH_PUSHBIKE_PEDAL = 136
    | INPUT_VEH_PUSHBIKE_SPRINT = 137
    | INPUT_VEH_PUSHBIKE_FRONT_BRAKE = 138
    | INPUT_VEH_PUSHBIKE_REAR_BRAKE = 139
    | INPUT_MELEE_ATTACK_LIGHT = 140
    | INPUT_MELEE_ATTACK_HEAVY = 141
    | INPUT_MELEE_ATTACK_ALTERNATE = 142
    | INPUT_MELEE_BLOCK = 143
    | INPUT_PARACHUTE_DEPLOY = 144
    | INPUT_PARACHUTE_DETACH = 145
    | INPUT_PARACHUTE_TURN_LR = 146
    | INPUT_PARACHUTE_TURN_LEFT_ONLY = 147
    | INPUT_PARACHUTE_TURN_RIGHT_ONLY = 148
    | INPUT_PARACHUTE_PITCH_UD = 149
    | INPUT_PARACHUTE_PITCH_UP_ONLY = 150
    | INPUT_PARACHUTE_PITCH_DOWN_ONLY = 151
    | INPUT_PARACHUTE_BRAKE_LEFT = 152
    | INPUT_PARACHUTE_BRAKE_RIGHT = 153
    | INPUT_PARACHUTE_SMOKE = 154
    | INPUT_PARACHUTE_PRECISION_LANDING = 155
    | INPUT_MAP = 156
    | INPUT_SELECT_WEAPON_UNARMED = 157
    | INPUT_SELECT_WEAPON_MELEE = 158
    | INPUT_SELECT_WEAPON_HANDGUN = 159
    | INPUT_SELECT_WEAPON_SHOTGUN = 160
    | INPUT_SELECT_WEAPON_SMG = 161
    | INPUT_SELECT_WEAPON_AUTO_RIFLE = 162
    | INPUT_SELECT_WEAPON_SNIPER = 163
    | INPUT_SELECT_WEAPON_HEAVY = 164
    | INPUT_SELECT_WEAPON_SPECIAL = 165
    | INPUT_SELECT_CHARACTER_MICHAEL = 166
    | INPUT_SELECT_CHARACTER_FRANKLIN = 167
    | INPUT_SELECT_CHARACTER_TREVOR = 168
    | INPUT_SELECT_CHARACTER_MULTIPLAYER = 169
    | INPUT_SAVE_REPLAY_CLIP = 170
    | INPUT_SPECIAL_ABILITY_PC = 171
    | INPUT_CELLPHONE_UP = 172
    | INPUT_CELLPHONE_DOWN = 173
    | INPUT_CELLPHONE_LEFT = 174
    | INPUT_CELLPHONE_RIGHT = 175
    | INPUT_CELLPHONE_SELECT = 176
    | INPUT_CELLPHONE_CANCEL = 177
    | INPUT_CELLPHONE_OPTION = 178
    | INPUT_CELLPHONE_EXTRA_OPTION = 179
    | INPUT_CELLPHONE_SCROLL_FORWARD = 180
    | INPUT_CELLPHONE_SCROLL_BACKWARD = 181
    | INPUT_CELLPHONE_CAMERA_FOCUS_LOCK = 182
    | INPUT_CELLPHONE_CAMERA_GRID = 183
    | INPUT_CELLPHONE_CAMERA_SELFIE = 184
    | INPUT_CELLPHONE_CAMERA_DOF = 185
    | INPUT_CELLPHONE_CAMERA_EXPRESSION = 186
    | INPUT_FRONTEND_DOWN = 187
    | INPUT_FRONTEND_UP = 188
    | INPUT_FRONTEND_LEFT = 189
    | INPUT_FRONTEND_RIGHT = 190
    | INPUT_FRONTEND_RDOWN = 191
    | INPUT_FRONTEND_RUP = 192
    | INPUT_FRONTEND_RLEFT = 193
    | INPUT_FRONTEND_RRIGHT = 194
    | INPUT_FRONTEND_AXIS_X = 195
    | INPUT_FRONTEND_AXIS_Y = 196
    | INPUT_FRONTEND_RIGHT_AXIS_X = 197
    | INPUT_FRONTEND_RIGHT_AXIS_Y = 198
    | INPUT_FRONTEND_PAUSE = 199
    | INPUT_FRONTEND_PAUSE_ALTERNATE = 200
    | INPUT_FRONTEND_ACCEPT = 201
    | INPUT_FRONTEND_CANCEL = 202
    | INPUT_FRONTEND_X = 203
    | INPUT_FRONTEND_Y = 204
    | INPUT_FRONTEND_LB = 205
    | INPUT_FRONTEND_RB = 206
    | INPUT_FRONTEND_LT = 207
    | INPUT_FRONTEND_RT = 208
    | INPUT_FRONTEND_LS = 209
    | INPUT_FRONTEND_RS = 210
    | INPUT_FRONTEND_LEADERBOARD = 211
    | INPUT_FRONTEND_SOCIAL_CLUB = 212
    | INPUT_FRONTEND_SOCIAL_CLUB_SECONDARY = 213
    | INPUT_FRONTEND_DELETE = 214
    | INPUT_FRONTEND_ENDSCREEN_ACCEPT = 215
    | INPUT_FRONTEND_ENDSCREEN_EXPAND = 216
    | INPUT_FRONTEND_SELECT = 217
    | INPUT_SCRIPT_LEFT_AXIS_X = 218
    | INPUT_SCRIPT_LEFT_AXIS_Y = 219
    | INPUT_SCRIPT_RIGHT_AXIS_X = 220
    | INPUT_SCRIPT_RIGHT_AXIS_Y = 221
    | INPUT_SCRIPT_RUP = 222
    | INPUT_SCRIPT_RDOWN = 223
    | INPUT_SCRIPT_RLEFT = 224
    | INPUT_SCRIPT_RRIGHT = 225
    | INPUT_SCRIPT_LB = 226
    | INPUT_SCRIPT_RB = 227
    | INPUT_SCRIPT_LT = 228
    | INPUT_SCRIPT_RT = 229
    | INPUT_SCRIPT_LS = 230
    | INPUT_SCRIPT_RS = 231
    | INPUT_SCRIPT_PAD_UP = 232
    | INPUT_SCRIPT_PAD_DOWN = 233
    | INPUT_SCRIPT_PAD_LEFT = 234
    | INPUT_SCRIPT_PAD_RIGHT = 235
    | INPUT_SCRIPT_SELECT = 236
    | INPUT_CURSOR_ACCEPT = 237
    | INPUT_CURSOR_CANCEL = 238
    | INPUT_CURSOR_X = 239
    | INPUT_CURSOR_Y = 240
    | INPUT_CURSOR_SCROLL_UP = 241
    | INPUT_CURSOR_SCROLL_DOWN = 242
    | INPUT_ENTER_CHEAT_CODE = 243
    | INPUT_INTERACTION_MENU = 244
    | INPUT_MP_TEXT_CHAT_ALL = 245
    | INPUT_MP_TEXT_CHAT_TEAM = 246
    | INPUT_MP_TEXT_CHAT_FRIENDS = 247
    | INPUT_MP_TEXT_CHAT_CREW = 248
    | INPUT_PUSH_TO_TALK = 249
    | INPUT_CREATOR_LS = 250
    | INPUT_CREATOR_RS = 251
    | INPUT_CREATOR_LT = 252
    | INPUT_CREATOR_RT = 253
    | INPUT_CREATOR_MENU_TOGGLE = 254
    | INPUT_CREATOR_ACCEPT = 255
    | INPUT_CREATOR_DELETE = 256
    | INPUT_ATTACK2 = 257
    | INPUT_RAPPEL_JUMP = 258
    | INPUT_RAPPEL_LONG_JUMP = 259
    | INPUT_RAPPEL_SMASH_WINDOW = 260
    | INPUT_PREV_WEAPON = 261
    | INPUT_NEXT_WEAPON = 262
    | INPUT_MELEE_ATTACK1 = 263
    | INPUT_MELEE_ATTACK2 = 264
    | INPUT_WHISTLE = 265
    | INPUT_MOVE_LEFT = 266
    | INPUT_MOVE_RIGHT = 267
    | INPUT_MOVE_UP = 268
    | INPUT_MOVE_DOWN = 269
    | INPUT_LOOK_LEFT = 270
    | INPUT_LOOK_RIGHT = 271
    | INPUT_LOOK_UP = 272
    | INPUT_LOOK_DOWN = 273
    | INPUT_SNIPER_ZOOM_IN = 274
    | INPUT_SNIPER_ZOOM_OUT = 275
    | INPUT_SNIPER_ZOOM_IN_ALTERNATE = 276
    | INPUT_SNIPER_ZOOM_OUT_ALTERNATE = 277
    | INPUT_VEH_MOVE_LEFT = 278
    | INPUT_VEH_MOVE_RIGHT = 279
    | INPUT_VEH_MOVE_UP = 280
    | INPUT_VEH_MOVE_DOWN = 281
    | INPUT_VEH_GUN_LEFT = 282
    | INPUT_VEH_GUN_RIGHT = 283
    | INPUT_VEH_GUN_UP = 284
    | INPUT_VEH_GUN_DOWN = 285
    | INPUT_VEH_LOOK_LEFT = 286
    | INPUT_VEH_LOOK_RIGHT = 287
    | INPUT_REPLAY_START_STOP_RECORDING = 288
    | INPUT_REPLAY_START_STOP_RECORDING_SECONDARY = 289
    | INPUT_SCALED_LOOK_LR = 290
    | INPUT_SCALED_LOOK_UD = 291
    | INPUT_SCALED_LOOK_UP_ONLY = 292
    | INPUT_SCALED_LOOK_DOWN_ONLY = 293
    | INPUT_SCALED_LOOK_LEFT_ONLY = 294
    | INPUT_SCALED_LOOK_RIGHT_ONLY = 295
    | INPUT_REPLAY_MARKER_DELETE = 296
    | INPUT_REPLAY_CLIP_DELETE = 297
    | INPUT_REPLAY_PAUSE = 298
    | INPUT_REPLAY_REWIND = 299
    | INPUT_REPLAY_FFWD = 300
    | INPUT_REPLAY_NEWMARKER = 301
    | INPUT_REPLAY_RECORD = 302
    | INPUT_REPLAY_SCREENSHOT = 303
    | INPUT_REPLAY_HIDEHUD = 304
    | INPUT_REPLAY_STARTPOINT = 305
    | INPUT_REPLAY_ENDPOINT = 306
    | INPUT_REPLAY_ADVANCE = 307
    | INPUT_REPLAY_BACK = 308
    | INPUT_REPLAY_TOOLS = 309
    | INPUT_REPLAY_RESTART = 310
    | INPUT_REPLAY_SHOWHOTKEY = 311
    | INPUT_REPLAY_CYCLEMARKERLEFT = 312
    | INPUT_REPLAY_CYCLEMARKERRIGHT = 313
    | INPUT_REPLAY_FOVINCREASE = 314
    | INPUT_REPLAY_FOVDECREASE = 315
    | INPUT_REPLAY_CAMERAUP = 316
    | INPUT_REPLAY_CAMERADOWN = 317
    | INPUT_REPLAY_SAVE = 318
    | INPUT_REPLAY_TOGGLETIME = 319
    | INPUT_REPLAY_TOGGLETIPS = 320
    | INPUT_REPLAY_PREVIEW = 321
    | INPUT_REPLAY_TOGGLE_TIMELINE = 322
    | INPUT_REPLAY_TIMELINE_PICKUP_CLIP = 323
    | INPUT_REPLAY_TIMELINE_DUPLICATE_CLIP = 324
    | INPUT_REPLAY_TIMELINE_PLACE_CLIP = 325
    | INPUT_REPLAY_CTRL = 326
    | INPUT_REPLAY_TIMELINE_SAVE = 327
    | INPUT_REPLAY_PREVIEW_AUDIO = 328
    | INPUT_VEH_DRIVE_LOOK = 329
    | INPUT_VEH_DRIVE_LOOK2 = 330
    | INPUT_VEH_FLY_ATTACK2 = 331
    | INPUT_RADIO_WHEEL_UD = 332
    | INPUT_RADIO_WHEEL_LR = 333
    | INPUT_VEH_SLOWMO_UD = 334
    | INPUT_VEH_SLOWMO_UP_ONLY = 335
    | INPUT_VEH_SLOWMO_DOWN_ONLY = 336
    | INPUT_VEH_HYDRAULICS_CONTROL_TOGGLE = 337
    | INPUT_VEH_HYDRAULICS_CONTROL_LEFT = 338
    | INPUT_VEH_HYDRAULICS_CONTROL_RIGHT = 339
    | INPUT_VEH_HYDRAULICS_CONTROL_UP = 340
    | INPUT_VEH_HYDRAULICS_CONTROL_DOWN = 341
    | INPUT_VEH_HYDRAULICS_CONTROL_UD = 342
    | INPUT_VEH_HYDRAULICS_CONTROL_LR = 343
    | INPUT_SWITCH_VISOR = 344
    | INPUT_VEH_MELEE_HOLD = 345
    | INPUT_VEH_MELEE_LEFT = 346
    | INPUT_VEH_MELEE_RIGHT = 347
    | INPUT_MAP_POI = 348
    | INPUT_REPLAY_SNAPMATIC_PHOTO = 349
    | INPUT_VEH_CAR_JUMP = 350
    | INPUT_VEH_ROCKET_BOOST = 351
    | INPUT_VEH_FLY_BOOST = 352
    | INPUT_VEH_PARACHUTE = 353
    | INPUT_VEH_BIKE_WINGS = 354
    | INPUT_VEH_FLY_BOMB_BAY = 355
    | INPUT_VEH_FLY_COUNTER = 356
    | INPUT_VEH_TRANSFORM = 357
    | INPUT_QUAD_LOCO_REVERSE = 358
    | INPUT_RESPAWN_FASTER = 359
    | INPUT_HUDMARKER_SELECT = 360


[<RequireQualifiedAccess>]
type GameControlGroup =
    | PLAYER_CONTROL = 0
    | FRONTEND_CONTROL = 2

[<RequireQualifiedAccess>]
type GameControlReadMode =
    | Enabled = 0
    | DisabledAware = 1

[<RequireQualifiedAccess>]
type InputDeviceKind =
    | Keyboard = 0
    | Mouse = 1
    | Controller = 2

[<RequireQualifiedAccess>]
type InputControlKind =
    | Button = 0
    | Axis = 1

[<RequireQualifiedAccess>]
type InputOriginKind =
    | None = 0
    | GameControl = 1
    | Keyboard = 2
    | Mouse = 3
    | Controller = 4

[<RequireQualifiedAccess>]
type KeyboardKey =
    | Backspace = 0x08
    | Tab = 0x09
    | Enter = 0x0D
    | Shift = 0x10
    | Control = 0x11
    | Alt = 0x12
    | Pause = 0x13
    | CapsLock = 0x14
    | Escape = 0x1B
    | Space = 0x20
    | PageUp = 0x21
    | PageDown = 0x22
    | End = 0x23
    | Home = 0x24
    | Left = 0x25
    | Up = 0x26
    | Right = 0x27
    | Down = 0x28
    | Insert = 0x2D
    | Delete = 0x2E
    | D0 = 0x30
    | D1 = 0x31
    | D2 = 0x32
    | D3 = 0x33
    | D4 = 0x34
    | D5 = 0x35
    | D6 = 0x36
    | D7 = 0x37
    | D8 = 0x38
    | D9 = 0x39
    | A = 0x41
    | B = 0x42
    | C = 0x43
    | D = 0x44
    | E = 0x45
    | F = 0x46
    | G = 0x47
    | H = 0x48
    | I = 0x49
    | J = 0x4A
    | K = 0x4B
    | L = 0x4C
    | M = 0x4D
    | N = 0x4E
    | O = 0x4F
    | P = 0x50
    | Q = 0x51
    | R = 0x52
    | S = 0x53
    | T = 0x54
    | U = 0x55
    | V = 0x56
    | W = 0x57
    | X = 0x58
    | Y = 0x59
    | Z = 0x5A
    | NumPad0 = 0x60
    | NumPad1 = 0x61
    | NumPad2 = 0x62
    | NumPad3 = 0x63
    | NumPad4 = 0x64
    | NumPad5 = 0x65
    | NumPad6 = 0x66
    | NumPad7 = 0x67
    | NumPad8 = 0x68
    | NumPad9 = 0x69
    | Multiply = 0x6A
    | Add = 0x6B
    | Subtract = 0x6D
    | Decimal = 0x6E
    | Divide = 0x6F
    | F1 = 0x70
    | F2 = 0x71
    | F3 = 0x72
    | F4 = 0x73
    | F5 = 0x74
    | F6 = 0x75
    | F7 = 0x76
    | F8 = 0x77
    | F9 = 0x78
    | F10 = 0x79
    | F11 = 0x7A
    | F12 = 0x7B
    | F13 = 0x7C
    | F14 = 0x7D
    | F15 = 0x7E
    | F16 = 0x7F
    | F17 = 0x80
    | F18 = 0x81
    | F19 = 0x82
    | F20 = 0x83
    | F21 = 0x84
    | F22 = 0x85
    | F23 = 0x86
    | F24 = 0x87
    | NumLock = 0x90
    | ScrollLock = 0x91
    | LeftShift = 0xA0
    | RightShift = 0xA1
    | LeftControl = 0xA2
    | RightControl = 0xA3
    | LeftAlt = 0xA4
    | RightAlt = 0xA5

[<RequireQualifiedAccess>]
type MouseButton =
    | Left = 0
    | Right = 1
    | Middle = 2
    | X1 = 3
    | X2 = 4

[<RequireQualifiedAccess>]
type MouseAxis =
    | X = 0
    | Y = 1

[<RequireQualifiedAccess>]
type ControllerButton =
    | DPadUp = 0
    | DPadDown = 1
    | DPadLeft = 2
    | DPadRight = 3
    | MenuPrimary = 4
    | MenuSecondary = 5
    | LeftStick = 6
    | RightStick = 7
    | LeftShoulder = 8
    | RightShoulder = 9
    | South = 10
    | East = 11
    | West = 12
    | North = 13

[<RequireQualifiedAccess>]
type ControllerAxis =
    | LeftStickX = 0
    | LeftStickY = 1
    | RightStickX = 2
    | RightStickY = 3
    | LeftTrigger = 4
    | RightTrigger = 5

[<Struct; IsReadOnly>]
type GameControl =
    val Group: int
    val Index: int

    new(group: int, index: int) =
        if group < 0 then
            invalidArg (nameof group) "A GTA control group cannot be negative."
        if index < 0 then
            invalidArg (nameof index) "A GTA control index cannot be negative."
        { Group = group; Index = index }

    new(group: GameControlGroup, control: GameControlId) =
        GameControl(int group, int control)

    static member Player(control: GameControlId) =
        GameControl(GameControlGroup.PLAYER_CONTROL, control)

    static member Frontend(control: GameControlId) =
        GameControl(GameControlGroup.FRONTEND_CONTROL, control)

[<Struct; IsReadOnly>]
type DeviceControl =
    val DeviceKind: InputDeviceKind
    val ControlKind: InputControlKind
    val Code: int

    new(deviceKind, controlKind, code) =
        if code < 0 then
            invalidArg (nameof code) "An input code cannot be negative."
        { DeviceKind = deviceKind
          ControlKind = controlKind
          Code = code }

    static member Keyboard(virtualKey: int) =
        if virtualKey < 1 || virtualKey > 255 then
            invalidArg
                (nameof virtualKey)
                "A Windows virtual-key code must be in the range 1 through 255."
        DeviceControl(InputDeviceKind.Keyboard, InputControlKind.Button, virtualKey)

    static member Keyboard(key: KeyboardKey) =
        DeviceControl.Keyboard(int key)

    static member Mouse(button: MouseButton) =
        DeviceControl(InputDeviceKind.Mouse, InputControlKind.Button, int button)

    static member Mouse(axis: MouseAxis) =
        DeviceControl(InputDeviceKind.Mouse, InputControlKind.Axis, int axis)

    static member Controller(button: ControllerButton) =
        DeviceControl(InputDeviceKind.Controller, InputControlKind.Button, int button)

    static member Controller(axis: ControllerAxis) =
        DeviceControl(InputDeviceKind.Controller, InputControlKind.Axis, int axis)

[<Struct; IsReadOnly>]
type InputOrigin =
    val Kind: InputOriginKind
    val ControlKind: InputControlKind
    val Group: int
    val Code: int

    new(kind, controlKind, group, code) =
        { Kind = kind
          ControlKind = controlKind
          Group = group
          Code = code }

    static member None =
        InputOrigin(
            InputOriginKind.None,
            InputControlKind.Button,
            0,
            0)

    static member Game(control: GameControl) =
        InputOrigin(
            InputOriginKind.GameControl,
            InputControlKind.Button,
            control.Group,
            control.Index)

    static member Device(control: DeviceControl) =
        let kind =
            match control.DeviceKind with
            | InputDeviceKind.Keyboard -> InputOriginKind.Keyboard
            | InputDeviceKind.Mouse -> InputOriginKind.Mouse
            | InputDeviceKind.Controller -> InputOriginKind.Controller
            | _ -> InputOriginKind.None

        InputOrigin(kind, control.ControlKind, 0, control.Code)

[<Struct; IsReadOnly>]
type InputState =
    val IsAvailable: bool
    val IsDown: bool
    val WasPressed: bool
    val WasReleased: bool
    val Value: single
    val FrameIndex: uint64
    val Origin: InputOrigin

    new(isAvailable, isDown, wasPressed, wasReleased, value, frameIndex, origin) =
        { IsAvailable = isAvailable
          IsDown = isDown
          WasPressed = wasPressed
          WasReleased = wasReleased
          Value = value
          FrameIndex = frameIndex
          Origin = origin }

    static member Unavailable(frameIndex: uint64, origin: InputOrigin) =
        InputState(false, false, false, false, 0.0f, frameIndex, origin)

[<Struct; IsReadOnly>]
type InputFrame =
    val FrameIndex: uint64
    val PerformanceCounter: int64
    val PerformanceFrequency: uint64
    val IsForeground: bool
    val IsUsingKeyboardAndMouse: bool

    new(frameIndex, performanceCounter, performanceFrequency, isForeground, isUsingKeyboardAndMouse) =
        { FrameIndex = frameIndex
          PerformanceCounter = performanceCounter
          PerformanceFrequency = performanceFrequency
          IsForeground = isForeground
          IsUsingKeyboardAndMouse = isUsingKeyboardAndMouse }

    static member Empty =
        InputFrame(0UL, 0L, 0UL, false, false)

[<AbstractClass>]
type InputBinding() =
    class
    end

[<Sealed>]
type GameControlBinding(control: GameControl, mode: GameControlReadMode) =
    inherit InputBinding()

    member _.Control = control
    member _.Mode = mode

    new(control: GameControl) =
        GameControlBinding(control, GameControlReadMode.Enabled)

[<Sealed>]
type DeviceControlBinding(control: DeviceControl) =
    inherit InputBinding()
    member _.Control = control

module private BindingSyntax =
    let private defaultGameControl (control: GameControlId) =
        let name = control.ToString()
        if name.StartsWith("INPUT_FRONTEND_", StringComparison.Ordinal) ||
           name.StartsWith("INPUT_CURSOR_", StringComparison.Ordinal) then
            GameControl.Frontend(control)
        else
            GameControl.Player(control)

    let private parseGameControl (token: string) =
        let mutable control = Unchecked.defaultof<GameControlId>
        if Enum.TryParse<GameControlId>(token, true, &control) &&
           Enum.IsDefined(typeof<GameControlId>, control) then
            GameControlBinding(defaultGameControl control) :> InputBinding
        else
            raise (
                FormatException(
                    $"Unknown GTA game input '{token}'. INPUT_* names must match the SHI game-control catalog."))

    let private tryParseKeyboard (token: string) =
        let normalized = token.Trim()
        let alias =
            match normalized.ToUpperInvariant() with
            | "ESC" -> "Escape"
            | "RETURN" -> "Enter"
            | "CTRL" -> "Control"
            | value when value.Length = 1 && Char.IsDigit(value[0]) -> "D" + value
            | _ -> normalized

        let mutable key = Unchecked.defaultof<KeyboardKey>
        if Enum.TryParse<KeyboardKey>(alias, true, &key) &&
           Enum.IsDefined(typeof<KeyboardKey>, key) then
            ValueSome(DeviceControlBinding(DeviceControl.Keyboard(key)) :> InputBinding)
        else
            ValueNone

    let private tryParseMouse (token: string) =
        match token.ToUpperInvariant() with
        | "MOUSE_LEFT" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Left)) :> InputBinding)
        | "MOUSE_RIGHT" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Right)) :> InputBinding)
        | "MOUSE_MIDDLE" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Middle)) :> InputBinding)
        | "MOUSE_X1" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X1)) :> InputBinding)
        | "MOUSE_X2" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X2)) :> InputBinding)
        | "MOUSE_AXIS_X" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.X)) :> InputBinding)
        | "MOUSE_AXIS_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.Y)) :> InputBinding)
        | _ -> ValueNone

    let private tryParseController (token: string) =
        match token.ToUpperInvariant() with
        | "PAD_DPAD_UP" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadUp)) :> InputBinding)
        | "PAD_DPAD_DOWN" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadDown)) :> InputBinding)
        | "PAD_DPAD_LEFT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadLeft)) :> InputBinding)
        | "PAD_DPAD_RIGHT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadRight)) :> InputBinding)
        | "PAD_MENU_PRIMARY" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuPrimary)) :> InputBinding)
        | "PAD_MENU_SECONDARY" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuSecondary)) :> InputBinding)
        | "PAD_LEFT_STICK" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftStick)) :> InputBinding)
        | "PAD_RIGHT_STICK" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightStick)) :> InputBinding)
        | "PAD_LEFT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftShoulder)) :> InputBinding)
        | "PAD_RIGHT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightShoulder)) :> InputBinding)
        | "PAD_SOUTH" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.South)) :> InputBinding)
        | "PAD_EAST" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.East)) :> InputBinding)
        | "PAD_WEST" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.West)) :> InputBinding)
        | "PAD_NORTH" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.North)) :> InputBinding)
        | "PAD_LEFT_STICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickX)) :> InputBinding)
        | "PAD_LEFT_STICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickY)) :> InputBinding)
        | "PAD_RIGHT_STICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickX)) :> InputBinding)
        | "PAD_RIGHT_STICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickY)) :> InputBinding)
        | "PAD_LEFT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftTrigger)) :> InputBinding)
        | "PAD_RIGHT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightTrigger)) :> InputBinding)
        | _ -> ValueNone

    let parse (text: string) =
        if String.IsNullOrWhiteSpace text then
            raise (FormatException("An input token cannot be empty."))

        let token = text.Trim()
        if token.StartsWith("INPUT_", StringComparison.OrdinalIgnoreCase) then
            parseGameControl token
        else
            match tryParseKeyboard token with
            | ValueSome binding -> binding
            | ValueNone ->
                match tryParseMouse token with
                | ValueSome binding -> binding
                | ValueNone ->
                    match tryParseController token with
                    | ValueSome binding -> binding
                    | ValueNone ->
                        raise (
                            FormatException(
                                $"Unknown device input '{token}'. Use a symbolic keyboard name, MOUSE_*, PAD_*, or an INPUT_* game control."))

    let format (binding: InputBinding) =
        ArgumentNullException.ThrowIfNull(binding)
        match binding with
        | :? GameControlBinding as game ->
            enum<GameControlId>(game.Control.Index).ToString()
        | :? DeviceControlBinding as device ->
            let control = device.Control
            match control.DeviceKind, control.ControlKind with
            | InputDeviceKind.Keyboard, InputControlKind.Button ->
                let key = enum<KeyboardKey>(control.Code)
                match key with
                | KeyboardKey.D0 -> "0"
                | KeyboardKey.D1 -> "1"
                | KeyboardKey.D2 -> "2"
                | KeyboardKey.D3 -> "3"
                | KeyboardKey.D4 -> "4"
                | KeyboardKey.D5 -> "5"
                | KeyboardKey.D6 -> "6"
                | KeyboardKey.D7 -> "7"
                | KeyboardKey.D8 -> "8"
                | KeyboardKey.D9 -> "9"
                | _ -> key.ToString()
            | InputDeviceKind.Mouse, InputControlKind.Button ->
                match enum<MouseButton>(control.Code) with
                | MouseButton.Left -> "MOUSE_LEFT"
                | MouseButton.Right -> "MOUSE_RIGHT"
                | MouseButton.Middle -> "MOUSE_MIDDLE"
                | MouseButton.X1 -> "MOUSE_X1"
                | MouseButton.X2 -> "MOUSE_X2"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported mouse button."))
            | InputDeviceKind.Mouse, InputControlKind.Axis ->
                match enum<MouseAxis>(control.Code) with
                | MouseAxis.X -> "MOUSE_AXIS_X"
                | MouseAxis.Y -> "MOUSE_AXIS_Y"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported mouse axis."))
            | InputDeviceKind.Controller, InputControlKind.Button ->
                "PAD_" +
                (match enum<ControllerButton>(control.Code) with
                | ControllerButton.DPadUp -> "DPAD_UP"
                | ControllerButton.DPadDown -> "DPAD_DOWN"
                | ControllerButton.DPadLeft -> "DPAD_LEFT"
                | ControllerButton.DPadRight -> "DPAD_RIGHT"
                | ControllerButton.MenuPrimary -> "MENU_PRIMARY"
                | ControllerButton.MenuSecondary -> "MENU_SECONDARY"
                | ControllerButton.LeftStick -> "LEFT_STICK"
                | ControllerButton.RightStick -> "RIGHT_STICK"
                | ControllerButton.LeftShoulder -> "LEFT_SHOULDER"
                | ControllerButton.RightShoulder -> "RIGHT_SHOULDER"
                | ControllerButton.South -> "SOUTH"
                | ControllerButton.East -> "EAST"
                | ControllerButton.West -> "WEST"
                | ControllerButton.North -> "NORTH"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported controller button.")))
            | InputDeviceKind.Controller, InputControlKind.Axis ->
                "PAD_" +
                (match enum<ControllerAxis>(control.Code) with
                | ControllerAxis.LeftStickX -> "LEFT_STICK_X"
                | ControllerAxis.LeftStickY -> "LEFT_STICK_Y"
                | ControllerAxis.RightStickX -> "RIGHT_STICK_X"
                | ControllerAxis.RightStickY -> "RIGHT_STICK_Y"
                | ControllerAxis.LeftTrigger -> "LEFT_TRIGGER"
                | ControllerAxis.RightTrigger -> "RIGHT_TRIGGER"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported controller axis.")))
            | _ ->
                raise (ArgumentException("The device binding contains an unsupported control.", nameof binding))
        | _ ->
            raise (ArgumentException("The input binding type is not supported.", nameof binding))

[<AbstractClass; Sealed>]
type InputBindingCodec private () =
    static member Parse(text: string) = BindingSyntax.parse text

    static member ParseMany(text: string) : IReadOnlyList<InputBinding> =
        if String.IsNullOrWhiteSpace text then
            raise (FormatException("An input binding list cannot be empty."))

        let tokens = text.Split(',', StringSplitOptions.TrimEntries)
        if tokens |> Array.exists String.IsNullOrWhiteSpace then
            raise (FormatException("An input binding list cannot contain an empty comma-separated token."))

        let values = tokens |> Array.map BindingSyntax.parse
        System.Array.AsReadOnly(values) :> IReadOnlyList<InputBinding>

    static member Format(binding: InputBinding) = BindingSyntax.format binding

    static member FormatMany(bindings: IEnumerable<InputBinding>) =
        ArgumentNullException.ThrowIfNull(bindings)
        let values = bindings |> Seq.map BindingSyntax.format |> Seq.toArray
        if Array.isEmpty values then
            invalidArg (nameof bindings) "At least one input binding is required."
        String.Join(", ", values)

[<Sealed>]
type InputActionBinding(name: string, inputs: IEnumerable<InputBinding>) =
    let normalizedName =
        if String.IsNullOrWhiteSpace name then
            invalidArg (nameof name) "An input action name cannot be empty."
        name.Trim()

    let captured =
        ArgumentNullException.ThrowIfNull(inputs)
        let values = inputs |> Seq.toArray
        if Array.isEmpty values then
            invalidArg (nameof inputs) "An input action requires at least one binding."
        for value in values do
            ArgumentNullException.ThrowIfNull(value, nameof inputs)
        values

    let readOnlyInputs: IReadOnlyList<InputBinding> =
        System.Array.AsReadOnly(captured)

    member _.Name = normalizedName
    member _.Inputs = readOnlyInputs

    new(name: string, inputText: string) =
        InputActionBinding(name, InputBindingCodec.ParseMany(inputText))

    new(name: string, [<ParamArray>] inputs: InputBinding array) =
        InputActionBinding(name, inputs :> IEnumerable<InputBinding>)

type IInputAction =
    inherit IDisposable
    abstract Name: string
    abstract State: InputState

type IInputActions =
    abstract Create: binding: InputActionBinding -> IInputAction
    abstract Create: name: string * inputs: string -> IInputAction

type IScriptHookInput =
    inherit IInputActions
    abstract Frame: InputFrame
    abstract Parse: text: string -> InputBinding
    abstract ParseMany: text: string -> IReadOnlyList<InputBinding>
