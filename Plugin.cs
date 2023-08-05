﻿using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Logging;
using System.Linq;
using Dalamud.Game.ClientState.Fates;
using System.Reflection.Metadata.Ecma335;

namespace vfaux;

internal enum WeeklyPuzzleTexture
{
    // Background textures
    Hidden = 5,
    Blank = 6,
    Blocked = 9,
}

internal enum WeeklyPuzzlePrizeTexture
{
    TinyBox = 0,
    TinySwords = 1,
    TinyChest = 2,
    TinyCommander = 3,
    BoxTL = 4,
    BoxTR = 5,
    BoxBL = 6,
    BoxBR = 7,
    ChestTL = 8,
    ChestTR = 9,
    ChestBL = 10,
    ChestBR = 11,
    SwordsTL = 12,
    SwordsTR = 13,
    SwordsML = 14,
    SwordsMR = 15,
    SwordsBL = 16,
    SwordsBR = 17,
    Commander = 18,
}

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Easier Faux Hollows";

    public DalamudPluginInterface Dalamud { get; init; }
    public CommandManager CommandManager { get; init; }
    public ClientState ClientState { get; init; }
    public GameGui GameGui { get; init; }

    private BoardState _board = new();
    private Solver _solver = new();

    public WindowSystem WindowSystem = new("vfaux");
    private PluginWindow _wnd;

    public Plugin(DalamudPluginInterface dalamud, CommandManager commmandManager, ClientState clientState, GameGui gameGui)
    {
        Dalamud = dalamud;
        CommandManager = commmandManager;
        ClientState = clientState;
        GameGui = gameGui;

        _wnd = new(_board, _solver);
        WindowSystem.AddWindow(_wnd);
        CommandManager.AddHandler("/vfaux", new CommandInfo((_, _) => _wnd.IsOpen = true) { HelpMessage = "Show plugin window" });

        Dalamud.UiBuilder.Draw += Draw;
        Dalamud.UiBuilder.OpenConfigUi += () => _wnd.IsOpen = true;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler("/vfaux");
        WindowSystem.RemoveAllWindows();
        _wnd.Dispose();
    }

    private void Draw()
    {
        SyncWithGameState();
        WindowSystem.Draw();
    }

    private unsafe void SyncWithGameState()
    {
        if (ClientState.TerritoryType != 478) // Idyllshire
            return;

        var addon = (AddonWeeklyPuzzle*)GameGui.GetAddonByName("WeeklyPuzzle", 1);
        if (addon == null)
            return;

        if (!addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.UldManager.LoadedState != AtkLoadState.Loaded)
            return;

        var tileState = ReadTileStateFromAddon(addon);
        _board.Update(tileState);
        var solution = _solver.Solve(_board);
        var bestScore = solution.Where(s => s < 16).Max();
        if (bestScore == 0)
            bestScore = -1;
        UpdateAddonColors(addon, solution, bestScore);
    }

    private unsafe BoardState.Tile[] ReadTileStateFromAddon(AddonWeeklyPuzzle* addon)
    {
        var result = new BoardState.Tile[BoardState.Width * BoardState.Height];
        int tileIndex = 0;
        for (int y = 0; y < BoardState.Height; ++y)
        {
            for (int x = 0; x < BoardState.Width; ++x)
            {
                ref var tileState = ref result[tileIndex++];

                var tileButton = GetTileButton(addon, x, y);
                var tileBackgroundImage = GetBackgroundImageNode(tileButton);
                var tileIconImage = GetIconImageNode(tileButton);
                tileState = (WeeklyPuzzleTexture)tileBackgroundImage->PartId switch
                {
                    WeeklyPuzzleTexture.Hidden => BoardState.Tile.Hidden,
                    WeeklyPuzzleTexture.Blocked => BoardState.Tile.Blocked,
                    WeeklyPuzzleTexture.Blank => !tileIconImage->AtkResNode.IsVisible ? BoardState.Tile.Empty : (WeeklyPuzzlePrizeTexture)tileIconImage->PartId switch
                    {
                        WeeklyPuzzlePrizeTexture.BoxTL => BoardState.Tile.BoxTL,
                        WeeklyPuzzlePrizeTexture.BoxTR => BoardState.Tile.BoxTR,
                        WeeklyPuzzlePrizeTexture.BoxBL => BoardState.Tile.BoxBL,
                        WeeklyPuzzlePrizeTexture.BoxBR => BoardState.Tile.BoxBR,
                        WeeklyPuzzlePrizeTexture.ChestTL => BoardState.Tile.ChestTL,
                        WeeklyPuzzlePrizeTexture.ChestTR => BoardState.Tile.ChestTR,
                        WeeklyPuzzlePrizeTexture.ChestBL => BoardState.Tile.ChestBL,
                        WeeklyPuzzlePrizeTexture.ChestBR => BoardState.Tile.ChestBR,
                        WeeklyPuzzlePrizeTexture.SwordsTL => BoardState.Tile.SwordsTL,
                        WeeklyPuzzlePrizeTexture.SwordsTR => BoardState.Tile.SwordsTR,
                        WeeklyPuzzlePrizeTexture.SwordsML => BoardState.Tile.SwordsML,
                        WeeklyPuzzlePrizeTexture.SwordsMR => BoardState.Tile.SwordsMR,
                        WeeklyPuzzlePrizeTexture.SwordsBL => BoardState.Tile.SwordsBL,
                        WeeklyPuzzlePrizeTexture.SwordsBR => BoardState.Tile.SwordsBR,
                        WeeklyPuzzlePrizeTexture.Commander => BoardState.Tile.Commander,
                        _ => BoardState.Tile.Unknown
                    },
                    _ => BoardState.Tile.Unknown
                };

                if (tileState == BoardState.Tile.Unknown)
                    PluginLog.Error($"Unexpected tile state at {x}x{y}: bg={tileBackgroundImage->PartId}, icon={tileIconImage->PartId}");

                var rotation = tileIconImage->AtkResNode.Rotation;
                if (rotation < 0)
                    tileState |= BoardState.Tile.RotatedL;
                else if (rotation > 0)
                    tileState |= BoardState.Tile.RotatedR;
            }
        }
        return result;
    }

    private unsafe void UpdateAddonColors(AddonWeeklyPuzzle* addon, int[] solution, int bestScore)
    {
        int tileIndex = 0;
        for (int y = 0; y < BoardState.Height; ++y)
        {
            for (int x = 0; x < BoardState.Width; ++x)
            {
                var soln = solution[tileIndex++];
                var tileButton = GetTileButton(addon, x, y);
                var tileBackgroundImage = GetBackgroundImageNode(tileButton);
                var (r, g, b) = soln switch
                {
                    Solver.ConfirmedSword => (31, 174, 186),
                    Solver.ConfirmedBoxChest => (180, 173, 44),
                    _ => soln == bestScore ? (32, 143, 46) : (0, 0, 0)
                };
                tileBackgroundImage->AtkResNode.AddRed = (ushort)r;
                tileBackgroundImage->AtkResNode.AddGreen = (ushort)g;
                tileBackgroundImage->AtkResNode.AddBlue = (ushort)b;
            }
        }
    }

    private unsafe AtkComponentButton* GetTileButton(AddonWeeklyPuzzle* addon, int x, int y) => addon->GameBoard[y][x].Button;
    private unsafe AtkImageNode* GetBackgroundImageNode(AtkComponentButton* button) => (AtkImageNode*)button->AtkComponentBase.UldManager.NodeList[3];
    private unsafe AtkImageNode* GetIconImageNode(AtkComponentButton* button) => (AtkImageNode*)button->AtkComponentBase.UldManager.NodeList[6];
}