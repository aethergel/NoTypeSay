using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace NoTypeSay;

// ReSharper disable once UnusedType.Global
public sealed unsafe partial class Plugin : IDalamudPlugin
{
    // This is the ID for eat chicken plus the flag they use to indicate an emote ID
    // We use this as a sentinel for identifying a button we injected, since we don't
    // expect them to ever require a /eatchicken
    private const int EmoteActionId = 0x2000000 | 271;

    // This is the icon ID for the button
    private const int ConverseIconId = 64345;

    private readonly List<Entry> questEntries = [];

    private Hook<AtkEventListener.Delegates.ReceiveEvent>? receiveEventDetour;

    public Plugin()
    {
        AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        if (ClientState.IsLoggedIn)
        {
            // if we're loading _while_ logged in, they installed from in-game, so we can just draw
            // otherwise, our hooks will run once login completes
            Framework.Run(ForceRedraw);
        }
    }

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog PluginLog { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    private static StringArrayData* StringArray => AtkStage.Instance()->GetStringArrayData(StringArrayType.ToDoList);

    public void Dispose()
    {
        receiveEventDetour?.Disable();
        receiveEventDetour?.Dispose();

        AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        // We should avoid modifying these from arbitrary threads, so clean ourselves up on the main thread
        Framework.Run(() =>
        {
            var numberArray = ToDoListNumberArray.Instance();
            var sad = StringArray;

            var offset = 0;
            foreach (var e in questEntries) e.ResetTodoEntry(numberArray, sad, ref offset);
            ForceRedraw();
        });
    }

    private static void ForceRedraw()
    {
        StringArray->UpdateState = 1;
    }

    private void PreReqUpdate(AddonEvent type, AddonArgs addonArgs)
    {
        // Lazily load this hook so we can use the vtable instead of a sig
        if (receiveEventDetour == null)
        {
            var todoList = (AddonToDoList*)addonArgs.Addon.Address;
            receiveEventDetour =
                GameInteropProvider.HookFromAddress<AtkEventListener.Delegates.ReceiveEvent>(
                    (nint)todoList->VirtualTable->ReceiveEvent, ReceiveEventDetour);
            receiveEventDetour?.Enable();
        }

        ProcessUpdate();
    }

    private void ProcessUpdate()
    {
        var numArray = ToDoListNumberArray.Instance();
        var stringArray = ToDoListStringArray.Instance();

        // UI disabled, just leave
        if (!numArray->QuestListEnabled) return;

        var offset = 0;
        foreach (var e in questEntries) e.Dispose();
        questEntries.Clear();
        var entryCount = numArray->QuestCount;
        for (var i = 0; i < entryCount; i++)
        {
            var e = new Entry
            {
                Index = i,
                TitleText = Utf8String.FromSequence((byte*)stringArray->QuestTexts[i]),
                DetailText = Utf8String.FromSequence((byte*)stringArray->QuestTexts[entryCount + i]),
                ButtonCount = numArray->ButtonCountForQuest[i]
            };

            // Populate the existing data into our record
            for (var j = 0; j < e.ButtonCount; j++)
            {
                e.ItemIDs.Add(numArray->QuestButtonActionId[offset]);
                e.IconIDs.Add(numArray->QuestButtonIconID[offset]);
                e.Strings.Add(Utf8String.FromSequence((byte*)stringArray->QuestStatusMessages[offset]));
                offset++;
            }

            // If we have a /say message, modify the button
            var msg = QuestMessage(e.TitleText->ToString(), e.DetailText->ToString());
            if (msg != "")
            {
                e.Message = $"/say {msg}";
                e.Modified = true;
                // check to see if our button is still present
                // if it is, we don't want to add another button
                // if the UI is full of buttons already somehow, don't add one either
                if (e.ButtonCount < 4 && e.ItemIDs.LastOrDefault() != EmoteActionId)
                {
                    e.ItemIDs.Add(EmoteActionId);
                    e.IconIDs.Add(ConverseIconId);
                    e.Strings.Add(Utf8String.FromString(msg));
                    e.ButtonCount++;
                }
                else if (e.ItemIDs.LastOrDefault() == EmoteActionId)
                {
                    // In this case, our button is still loaded from before somehow, so just
                    // store the string for it on our entry
                    // Also ensure that the IconID is still correct (it probably is, but this is safer)
                    e.Strings[^1] = Utf8String.FromString(msg);
                    e.IconIDs[^1] = ConverseIconId;
                }
            }

            questEntries.Add(e);
        }

        if (questEntries.Count == 0) return;
        var sad = StringArray;
        offset = 0;
        foreach (var e in questEntries) e.WriteTodoEntry(numArray, sad, ref offset);
    }

    private void ReceiveEventDetour(
        AtkEventListener* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent,
        AtkEventData* atkEventData)
    {
        if (!HandleEvent(eventType, eventParam, atkEvent, atkEventData))
            receiveEventDetour?.Original.Invoke(thisPtr, eventType, eventParam, atkEvent, atkEventData);
    }

    private bool HandleEvent(AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData)
    {
        try
        {
            if (eventType != AtkEventType.MouseUp || eventParam != 1 || atkEventData->MouseData.ButtonId == 1)
                return false;

            // Figure out which quest entry this is
            var targetNode = (AtkResNode*)atkEvent->Target;
            if (targetNode == null || targetNode->ParentNode == null)
            {
                PluginLog.Debug("no parent node");
                return false;
            }

            var parentId = targetNode->ParentNode->NodeId;
            if (parentId <= 70000)
            {
                PluginLog.Debug("this isn't a title component");
                return false;
            }

            // adjust the node ID to get the entry ID
            parentId -= 70001;

            if (parentId >= questEntries.Count)
            {
                PluginLog.Debug($"overshot the entries somehow- {parentId} >= {questEntries.Count}");
                return false;
            }

            var entry = questEntries[(int)parentId];
            if (entry is not { Modified: true })
            {
                PluginLog.Debug("unmodified entry");
                return false;
            }

            // Now let's make sure we have the right button- we almost certainly do, since the button
            // is almost definitely the only one present on the entry, but let's be safe.
            // Yes, they store the event item/emote ID as the event's node pointer.
            if (atkEvent->Node == null)
            {
                PluginLog.Debug("no node param");
                return false;
            }

            var itemOrEmoteId = *(uint*)atkEvent->Node;
            if (itemOrEmoteId != EmoteActionId)
            {
                PluginLog.Debug("not our button- {0}", itemOrEmoteId);
                return false;
            }

            // This is our event
            UIGlobals.PlaySoundEffect(8);
            SendChat(entry.Message);
            return true;
        }
        catch (Exception ex)
        {
            // If something went wrong, log it and return true- we don't want to send this
            // button since it might be ours and you might not have /eatchicken
            PluginLog.Error($"Caught exception in handleEvent: {ex}");
            return true;
        }
    }

    private static void SendChat(string message)
    {
        var str = Utf8String.FromString(message);
        str->SanitizeString(AllowedEntities.Numbers | AllowedEntities.UppercaseLetters |
                            AllowedEntities.LowercaseLetters | AllowedEntities.OtherCharacters |
                            AllowedEntities.SpecialCharacters);
        // This is the max length the chatbox would allow someone to type in
        // so make sure we don't send anything longer than this
        if (str->StringLength > 500) return;
        UIModule.Instance()->ProcessChatBoxEntry(str);
    }

    // While we _could_ just keep a list of all /say quests, unless they
    // completely change the way they implement them, there's always a QuestDialogue
    // that contains _just_ the expected message, so we'll just look that up.
    // This approach also means we should support quests that have multiple /say steps
    private static string QuestMessage(string title, string detail)
    {
        var q = DataManager.GetExcelSheet<Quest>().FirstOrDefault(x => x.Name.ToString() == title);
        if (q.RowId == 0) return "";
        return TextSheetForQuest(q).FirstOrNull(qd => MessageMatch(detail, qd))?.Value.ToString() ?? "";
    }

    private static bool MessageMatch(string todoText, QuestDialogue qd)
    {
        if (!KeyRegex().IsMatch(qd.Key.ToString())) return false;
        if (qd.Value.IsEmpty) return false;
        var re = ClientState.ClientLanguage switch
        {
            ClientLanguage.English => EnFrRegex(),
            ClientLanguage.French => EnFrRegex(),
            ClientLanguage.Japanese => JpRegex(),
            ClientLanguage.German => DeRegex(),
            _ => EnFrRegex() // This shouldn't really happen, but in the worst case, just try this
        };
        var message = qd.Value.ToString();
        return re.Matches(todoText).Any(m => m.Captures[0].Value.Contains(message));
    }

    [GeneratedRegex("“([^”]+)”")]
    private static partial Regex EnFrRegex();

    [GeneratedRegex("「([^」])+」|『([^』]+)』")]
    private static partial Regex JpRegex();

    [GeneratedRegex("„([^“]+)“")]
    private static partial Regex DeRegex();

    [GeneratedRegex("_(SAY|SAYTODO|SYSTEM)_")]
    private static partial Regex KeyRegex();

    private static ExcelSheet<QuestDialogue> TextSheetForQuest(Quest q)
    {
        var qid = q.Id.ToString();
        var dir = qid.Substring(qid.Length - 5, 3);
        return DataManager.GetExcelSheet<QuestDialogue>(name: $"quest/{dir}/{qid}");
    }

    private class Entry : IDisposable
    {
        // These should never have more than 4 items
        internal readonly List<int> IconIDs = [];
        internal readonly List<int> ItemIDs = [];
        internal readonly List<Pointer<Utf8String>> Strings = [];

        internal int ButtonCount;
        internal Utf8String* DetailText; // used to id the quest step
        internal int Index;
        internal string Message = "";
        internal bool Modified;

        internal Utf8String* TitleText; // used to id the quest

        public void Dispose()
        {
            if (TitleText != null) TitleText->Dtor(true);
            if (DetailText != null) DetailText->Dtor(true);
            foreach (var s in Strings)
                if (s.Value != null)
                    s.Value->Dtor(true);
        }

        // ReSharper disable once UnusedMember.Local
        public new string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Index}: [{(Modified ? 'x' : ' ')}] {ButtonCount} {TitleText->ToString()}");
            return sb.ToString();
        }

        internal void ResetTodoEntry(ToDoListNumberArray* nad, StringArrayData* sad, ref int offset)
        {
            // If we modified it AND we have buttons to remove,
            // decrement the button count (we're always the last one)
            if (Modified && ButtonCount > 0)
            {
                ButtonCount--;
                Modified = false;
            }

            WriteTodoEntry(nad, sad, ref offset);
        }

        internal void WriteTodoEntry(ToDoListNumberArray* nad, StringArrayData* sad, ref int offset)
        {
            nad->ButtonCountForQuest[Index] = ButtonCount;
            for (var i = 0; i < ButtonCount; i++)
            {
                // Don't propagate the update here since we'll be making a lot of changes
                // Also, we're either already _about_ to process an update or we'll request
                // one when we're done
                nad->QuestButtonActionId[offset] = ItemIDs[i];
                nad->QuestButtonIconID[offset] = IconIDs[i];
                sad->SetValueUtf8(offset + 119, Strings[i], suppressUpdates: true);
                offset++;
            }
        }
    }
}
