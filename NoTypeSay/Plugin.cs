using System;
using System.Collections.Generic;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.Linq;
using System.Text;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace NoTypeSay;

// ReSharper disable once UnusedType.Global
public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    
    // This is the ID for eat chicken plus the flag they use to indicate an emote ID
    // We use this as a sentinel for identifying a button we injected, since we don't
    // expect them to ever require a /eatchicken
    private const int EmoteActionId = 0x2000000 | 271;
    
    // This is the icon ID for the button
    private const int ConverseIconId = 64345;

    public Plugin()
    {
        GameInteropProvider.InitializeFromAttributes(this);

        receiveEventDetour?.Enable();
        
        AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        if (ClientState.IsLoggedIn)
        {
            // if we're loading _while_ logged in, they installed from in-game, so we can just draw
            // otherwise, our hooks will run once login completes
            Framework.Run(ForceRedraw);
        }
    }


    public void Dispose()
    {
        receiveEventDetour?.Disable();
        receiveEventDetour?.Dispose();

        AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        // We should avoid modifying these from arbitrary threads, so clean ourselves up on the main thread
        Framework.Run(() =>
        {
            var nad = RaptureAtkModule.Instance()->GetNumberArrayData((int)NumberArrayType.ToDoList);
            var sad = RaptureAtkModule.Instance()->GetStringArrayData((int)StringArrayType.ToDoList);

            var offset = 0;
            foreach (var e in questEntries)
            {
                e.ResetTodoEntry(nad, sad, ref offset);
            }
            ForceRedraw();
        });
    }

    private static void ForceRedraw()
    {
        var nad = RaptureAtkModule.Instance()->AtkArrayDataHolder.GetNumberArrayData((int)NumberArrayType.ToDoList);
        // We aren't changing anything, but this will force the appropriate redraw to happen
        nad->SetValue(7, nad->IntArray[7], true);
    }

    private class Entry
    {
        internal int Index;
        internal bool Modified;
        internal string Message = "";

        internal Utf8String *TitleText; // used to id the quest
        internal Utf8String *DetailText; // used to id the quest step
        
        // These should never have more than 4 items
        internal int ButtonCount;
        internal readonly List<int> ItemIDs = [];
        internal readonly List<int> IconIDs = [];
        internal Utf8String*[] Strings = [];
        
        // ReSharper disable once UnusedMember.Local
        public new string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Index}: [{(Modified ? 'x' : ' ')}] {ButtonCount} {TitleText->ToString()}");
            return sb.ToString();
        }

        internal void ResetTodoEntry(NumberArrayData* nad, StringArrayData* sad, ref int offset)
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

        internal void WriteTodoEntry(NumberArrayData* nad, StringArrayData* sad, ref int offset)
        {
            nad->IntArray[Index + 39] = ButtonCount;
            for (var i = 0; i < ButtonCount; i++)
            {
                // Don't propagate the update here since we'll be making a lot of changes
                // Also, we're either already _about_ to process an update or we'll request
                // one when we're done
                nad->IntArray[offset+49] = ItemIDs[i];
                nad->IntArray[offset+89] = IconIDs[i];
                sad->SetValueUtf8(offset + 119, Strings[i], suppressUpdates:true);
                offset++;
            }
        }
    }

    private readonly List<Entry> questEntries = [];

    private void PreReqUpdate(AddonEvent type, AddonArgs addonArgs)
    {
        var args = (AddonRequestedUpdateArgs)addonArgs;
        ProcessUpdate((NumberArrayData**)args.NumberArrayData, (StringArrayData**)args.StringArrayData);
    }

    private void ProcessUpdate(NumberArrayData** numbers, StringArrayData** strings)
    {
        var nad = numbers[(int)NumberArrayType.ToDoList];
        var sad = strings[(int)StringArrayType.ToDoList];

        // UI disabled, just leave
        if (nad->IntArray[7] == 0) return;

        var offset = 0;
        questEntries.Clear();
        var entryCount = nad->IntArray[8];
        for (var i = 0; i < entryCount; i++)
        {
            var e = new Entry
            {
                Index = i,
                TitleText = Utf8String.FromSequence(sad->StringArray[9 + i]),
                DetailText = Utf8String.FromSequence(sad->StringArray[9 + entryCount + i]),
                ButtonCount = nad->IntArray[39 + i]
            };
            e.Strings = new Utf8String*[e.ButtonCount + 1]; // Always make room for one more in case I need to add
            for (var j = 0; j < e.ButtonCount; j++)
            {
                e.ItemIDs.Add(nad->IntArray[49 + offset]);
                e.IconIDs.Add(nad->IntArray[89 + offset]);
                e.Strings[j] = Utf8String.FromSequence(sad->StringArray[119 + offset]);
                offset++;
            }

            // check to see if our button is still present
            // if it is, we don't want to add another button
            // if the UI is full of buttons already somehow, don't add one either
            if (e.ButtonCount < 4 && e.ItemIDs.LastOrDefault() != EmoteActionId)
            {
                // Add our button if we have a say message
                var msg = QuestMessage(e.TitleText->ToString(), e.DetailText->ToString());
                if (msg != "")
                {
                    e.Message = $"/say {msg}";
                    e.ItemIDs.Add(EmoteActionId);
                    e.IconIDs.Add(ConverseIconId);
                    e.Strings[e.ButtonCount++] = Utf8String.FromString(msg);

                    e.Modified = true;
                }
            }

            questEntries.Add(e);
        }

        offset = 0;
        foreach (var e in questEntries)
        {
            e.WriteTodoEntry(nad, sad, ref offset);
        }
    }

    // Once AddonToDoList is added to ClientStructs, this can be replaced with a vtable hook,
    // but for now we'll use this probably-longer-than-necessary signature
    // ReSharper disable once FieldCanBeMadeReadOnly.Local
    [Signature("48 89 5C 24 ?? 55 56 57 41 56 41 57 48 8B EC 48 83 EC 70 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 F0 41 8B F8", DetourName = nameof(ReceiveEventDetour))]
    private Hook<AtkEventListener.Delegates.ReceiveEvent>? receiveEventDetour = null!;

    
    private void ReceiveEventDetour(AtkEventListener* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData)
    {
        if (!HandleEvent(eventType, eventParam, atkEvent, atkEventData))
        {
            receiveEventDetour?.Original.Invoke(thisPtr, eventType, eventParam, atkEvent, atkEventData);
        }
    }
    
    private bool HandleEvent(AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData) {
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

            // Now let's make sure we have the right button - we almost certainly do, since the button
            // is almost definitely the only one present on the entry, but let's be safe.
            // Yes, they store the event item/emote ID as the event's node pointer.
            var itemOrEmoteId = *(UInt32*)atkEvent->Node;
            if (itemOrEmoteId != (0x2000000 | 271))
            {
                PluginLog.Debug("not our button");
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
        str->SanitizeString(AllowedEntities.Numbers|AllowedEntities.UppercaseLetters|AllowedEntities.LowercaseLetters|AllowedEntities.OtherCharacters|AllowedEntities.SpecialCharacters);
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
        var row = TextSheetForQuest(q).FirstOrDefault(qt =>　MessageMatch(detail, qt.Value.ToString()));
        return row.RowId == 0 ? "" : row.Value.ToString();
    }

    private static bool MessageMatch(string todoText, string message)
    {
        if (todoText == message) return false; // we don't want to match against the current todo step
        return ClientState.ClientLanguage switch
        {       
            ClientLanguage.English => todoText.Contains($"“{message}”"),
            ClientLanguage.French => todoText.Contains($"“{message}”"),
            ClientLanguage.Japanese => todoText.Contains($"『{message}』") || todoText.Contains($"「{message}」"),
            ClientLanguage.German => todoText.Contains($"„{message}“"),
            _ => false
        };
    }
    
    private static ExcelSheet<QuestDialogue> TextSheetForQuest(Quest q)
    {
        var qid = q.Id.ToString();
        var dir = qid.Substring(qid.Length - 5, 3);
        return DataManager.GetExcelSheet<QuestDialogue>(name: $"quest/{dir}/{qid}");
    }
}
