﻿using Accord.Imaging.Filters;
using Accord.Math;
using Assistant;
using IronPython.Runtime;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.UI.WebControls;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using Ultima;
using WebSocketSharp;
using static IronPython.Modules._ast;
using static RazorEnhanced.Settings;

namespace RazorEnhanced.RCE
{
    public class RCEScriptError : Exception
    {
        public ASTNode Node;
        public string Type;
        public string Lexeme;
        public string Line;
        public int LineNumber;
        public string Error;

        public string ErrorName() { return this.GetType().Name; }

        public RCEScriptError(ASTNode node, string error) : base(error)
        {
            Node = node;
            Type = node?.Type.ToString() ?? "";
            Lexeme = node?.Lexeme ?? "";
            Line = node?.Lexer?.GetLine(node.LineNumber) ?? "";
            LineNumber = node?.LineNumber ?? -1;
            Error = error;
        }

        public RCEScriptError(string line, int lineNumber, ASTNode node, string error) : this(node, error)
        {
            Line = line;
            LineNumber = lineNumber;
        }

        public string GetErrorMessage()
        {
            string msg = string.Format("{0}: {1}\n", ErrorName(), Error);
            if (Node != null)
            {
                msg += String.Format("Type: {0}\n", Type);
                msg += String.Format("Word: {0}\n", Lexeme);
                msg += String.Format("Line: {0}\n", LineNumber + 1);
                msg += String.Format("Code: {0}\n", Line);
            }
            return msg;
        }

        public override string Message { get { return GetErrorMessage(); } }
    }


    public class RCESyntaxError : RCEScriptError
    {
        public RCESyntaxError(ASTNode node, string error) : base(node, error) { }
        public RCESyntaxError(string line, int lineNumber, ASTNode node, string error) : base(line, lineNumber, node, error) { }
    }

    public class RCERuntimeError : RCEScriptError
    {
        public RCERuntimeError(ASTNode node, string error) : base(node, error) { }
        public RCERuntimeError(string line, int lineNumber, ASTNode node, string error) : base(line, lineNumber, node, error) { }
    }

    public class RCEArgumentError : RCERuntimeError
    {
        public RCEArgumentError(ASTNode node, string error) : base(node, error) { }
        public RCEArgumentError(string line, int lineNumber, ASTNode node, string error) : base(line, lineNumber, node, error) { }
    }

    public class RCEStopError : RCEScriptError
    {
        public RCEStopError(ASTNode node, string error) : base(node, error) { }
        public RCEStopError(string line, int lineNumber, ASTNode node, string error) : base(line, lineNumber, node, error) { }
    }

    public class RunTimeError : Exception
    {
        public RunTimeError(string error) : base(error)
        {
        }
    }





    /// <summary>
    /// This class contains all the methods available for the RCE scripts (.rce) 
    /// The following classes and commands ARE NOT available in python.
    /// </summary>
    public class RazorCeEngine
    {
        static bool DEFAULT_ISOLATION = false;

        private int m_lastMount;
        private int m_toggle_LeftSave;
        private int m_toggle_RightSave;
        private Journal m_journal;
        //private Mutex m_mutex;
        private readonly Lexer m_Lexer = new Lexer();

        private bool m_Loaded = false;
        private bool m_UseIsolation = DEFAULT_ISOLATION;
        private Namespace m_Namespace;
        private Interpreter m_Interpreter;
        private RCECompiledScript m_Compiled;

        private Action<string> m_OutputWriter;
        private Action<string> m_ErrorWriter;

        public void SetTrace(RCETracebackDelegate traceDelegate)
        {
            m_Compiled.OnTraceback += traceDelegate;
        }

        public void SetStdout(Action<string> stdoutWriter)
        {
            m_OutputWriter = stdoutWriter;
        }

        public void SetStderr(Action<string> stderrWriter)
        {
            m_ErrorWriter = stderrWriter;
        }

        public RCECompiledScript Compiled { get { return m_Compiled; } }
        public Interpreter Interpreter { get { return m_Interpreter; } }
        public Namespace Namespace
        {
            get { return m_Namespace; }
            set
            {
                if (value != m_Namespace)
                {
                    m_Namespace = value;
                }
            }
        }

        public bool UseIsolation
        {
            get { return m_UseIsolation; }
            set
            {
                if (value != m_UseIsolation)
                {
                    m_UseIsolation = value;
                    UpdateNamespace();
                }
            }
        }



        // useOnceIgnoreList
        private readonly List<int> m_serialUseOnceIgnoreList;

        private static readonly string DEFAULT_FRIEND_LIST = "RCE";


        internal List<string> AllAliases()
        {
            var list = Interpreter._aliasHandlers.Keys.ToList();
            list.AddRange(m_Interpreter._alias.Keys.ToArray());
            return list;
        }

        internal List<string> AllKeywords()
        {
            List<string> retlist = new List<string>();
            foreach (var cmd in m_Interpreter._commandHandlers)
            {
                var handler = cmd.Value;
                if (handler.Method.Name == "ExpressionCommand")
                {
                    continue;
                }
                var documentation = XMLCommentReader.GetDocumentation(handler.Method);
                documentation = XMLCommentReader.RemoveBaseIndentation(documentation);
                var methodSummary = XMLCommentReader.ExtractXML(documentation, "summary");
                var returnDesc = XMLCommentReader.ExtractXML(documentation, "returns");
                if (methodSummary == "")
                {
                    retlist.Add(cmd.Key);
                }
                else
                {
                    retlist.AddRange(methodSummary.Split('\n').Where(line => line.Trim().Length > 0));
                }
            }

            foreach (var expr in m_Interpreter._exprHandlers)
            {
                var handler = expr.Value;
                if (handler.Method.Name == "ExpressionCommand")
                {
                    continue;
                }
                var documentation = XMLCommentReader.GetDocumentation(handler.Method);
                documentation = XMLCommentReader.RemoveBaseIndentation(documentation);
                var methodSummary = XMLCommentReader.ExtractXML(documentation, "summary");
                var returnDesc = XMLCommentReader.ExtractXML(documentation, "returns");
                if (methodSummary == "")
                {
                    retlist.Add(expr.Key);
                }
                else
                {
                    retlist.AddRange(methodSummary.Split('\n').Where(line => line.Trim().Length > 0));
                }
            }
            return retlist;
        }




        public RazorCeEngine()
        {
            //m_mutex = new Mutex();
            m_serialUseOnceIgnoreList = new List<int>();

            m_Loaded = false;
            m_toggle_LeftSave = 0;
            m_toggle_RightSave = 0;
            m_lastMount = 0;

            string configFile = System.IO.Path.Combine(Assistant.Engine.RootPath, "Config", "RCE.config.json");
            UpdateNamespace();
            InitInterpreter();
        }

        public void SendOutput(string text)
        {
            if (m_OutputWriter != null)
            {
                m_OutputWriter.Invoke(text);
            }
        }
        public void SendError(string text)
        {
            if (m_ErrorWriter != null)
            {
                m_ErrorWriter.Invoke(text);
            }
        }
        private void RegisterAlias()
        {
            m_Interpreter.RegisterAliasHandler("ground", AliasHandler);
            m_Interpreter.RegisterAliasHandler("any", AliasHandler);
            m_Interpreter.RegisterAliasHandler("backpack", AliasHandler);
            m_Interpreter.RegisterAliasHandler("self", AliasHandler);
            m_Interpreter.RegisterAliasHandler("bank", AliasHandler);
            m_Interpreter.RegisterAliasHandler("lasttarget", AliasHandler);
            m_Interpreter.RegisterAliasHandler("last", AliasHandler);
            m_Interpreter.RegisterAliasHandler("mount", AliasHandler);
            m_Interpreter.RegisterAliasHandler("lefthand", AliasHandler);
            m_Interpreter.RegisterAliasHandler("righthand", AliasHandler);
            //m_Interpreter.RegisterAliasHandler("lastobject", AliasHandler);
            //TODO: how to you get the "last object" in razor ?
            uint lastobject = 0;
            uint found = 0;
            uint enemy = 0;
            uint friend = 0;
            if (m_Namespace == Namespace.GlobalNamespace)
            {
                if (Misc.CheckSharedValue("lastobject"))
                    lastobject = (uint)Misc.ReadSharedValue("lastobject");
                if (Misc.CheckSharedValue("found"))
                    found = (uint)Misc.ReadSharedValue("found");
                if (Misc.CheckSharedValue("enemy"))
                    enemy = (uint)Misc.ReadSharedValue("enemy");
                if (Misc.CheckSharedValue("friend"))
                    friend = (uint)Misc.ReadSharedValue("friend");
            }
            m_Interpreter.SetAlias("lastobject", lastobject); //TODO: not implemented

            m_Interpreter.SetAlias("found", found);
            m_Interpreter.SetAlias("enemy", enemy);
            m_Interpreter.SetAlias("friend", friend);


        }

        static uint AliasHandler(string alias)
        {
            int everywhere = -1;
            try
            {
                switch (alias.ToLower())
                {
                    case "ground": return (uint)everywhere;
                    case "any": return (uint)everywhere;
                    case "backpack": return (uint)Player.Backpack.Serial;
                    case "self": return (uint)Player.Serial;
                    case "bank": return (uint)Player.Bank.Serial;
                    case "mount": return (uint)Player.Mount.Serial; //TODO: is this the real mount serial? in every server ?
                    case "lefthand":
                        {
                            Item i = Player.GetItemOnLayer("LeftHand");
                            if (i != null)
                                return (uint)i.Serial;
                            return 0;
                        }
                    case "righthand":
                        {
                            Item i = Player.GetItemOnLayer("RightHand");
                            if (i != null)
                                return (uint)i.Serial;
                            return 0;
                        }
                    case "lasttarget": return (uint)RazorEnhanced.Target.GetLast();
                    case "last": return (uint)RazorEnhanced.Target.GetLast();
                        //case "lastobject": return (uint)Items.LastLobject(); // TODO: Doesn't look like RE there is a way in RE to get the "last object" Serial
                }
            }
            catch (Exception e)
            { }
            return 0;
        }

        public void InitInterpreter(bool force = false)
        {
            if (force || m_Interpreter == null)
            {
                m_Interpreter = new(this);
                RegisterCommands();
                RegisterAlias();
            }
        }

        public bool Load(string filename)
        {
            try
            {
                var root = m_Lexer.Lex(filename);
                return Load(root, filename);
            }
            catch (Exception e)
            {
                SendError($"RCEEngine: fail to parse script:\n{e.Message}");
            }
            return false;
        }

        public bool Load(string content, string filename)
        {
            try
            {
                var lines = content.Split('\n');
                var root = m_Lexer.Lex(lines);
                return Load(root, filename);
            }
            catch (Exception e)
            {
                SendError($"RCEEngine: fail to parse script:\n{e.Message}");
            }
            return false;
        }

        public bool Load(ASTNode root, string filename = "")
        {
            //m_mutex.WaitOne();

            m_Loaded = false;
            m_Compiled = null;
            try
            {
                InitInterpreter(true);

                m_Compiled = new RCECompiledScript(root, this);
                m_Compiled.Filename = filename;


                UpdateNamespace();
                m_Loaded = true;
            }
            catch (Exception e)
            {
                SendError($"RCEEngine: fail to load script:\n{e.Message}");
            }

            //m_mutex.ReleaseMutex();
            return m_Loaded;
        }

        public bool Execute(bool editorMode = false)
        {
            if (!m_Loaded) { return false; }
            Execute();
            /*m_mutex.WaitOne();
            try
            {
                Execute();
            }
            catch (RCEStopError e)
            {
                EnhancedScript.Service.CurrentScript().Stop();
            }
            catch (Exception e)
            {
                EnhancedScript.Service.CurrentScript().Stop();
                throw e;
            }
            finally
            {
                //m_mutex.ReleaseMutex();
            }     
                */
            return true;
        }

        private void UpdateNamespace()
        {
            var ns = Namespace.GlobalNamespace;
            if (m_UseIsolation && m_Namespace == Namespace.GlobalNamespace)
            {
                ns = Namespace.Get(Path.GetFileNameWithoutExtension(Compiled.Filename));
            }
            if (ns != m_Namespace)
            {
                m_Namespace = ns;
            }
        }

        private void Execute()
        {
            m_journal = new Journal(100);

            m_Interpreter.StartScript();
            try
            {
                while (m_Interpreter.ExecuteScript()) { };
            }
            catch (Exception e)
            {
                m_Interpreter.StopScript();
                throw e;
            }
            finally
            {
                m_journal.Active = false;
            }
        }



        public static void WrongParameterCount(ASTNode node, int expected, int given, string message = "")
        {
            string command = node.Lexeme;
            var msg = String.Format("{0} expect {1} parameters, {2} given. {3}", command, expected, given, message);
            throw new RCEArgumentError(node, msg);
        }

        private void RegisterCommands()
        {
            #region commands
            // Commands based on Actions.cs
            Interpreter.RegisterCommandHandler("attack", Attack); //Attack by serial
            Interpreter.RegisterCommandHandler("cast", Cast); //BookcastAction, etc

            // Dress
            Interpreter.RegisterCommandHandler("dress", Dress); //DressAction
            Interpreter.RegisterCommandHandler("undress", Undress); //UndressAction

            // Using stuff
            Interpreter.RegisterCommandHandler("dclicktype", DClickType); // DoubleClickTypeAction
            Interpreter.RegisterCommandHandler("dclick", DClick); //DoubleClickAction

            Interpreter.RegisterCommandHandler("usetype", UseType); // DoubleClickTypeAction
            Interpreter.RegisterCommandHandler("useobject", UseObject); //DoubleClickAction

            // Moving stuff
            Interpreter.RegisterCommandHandler("drop", DropItem); //DropAction
            Interpreter.RegisterCommandHandler("droprelloc", DropRelLoc); //DropAction
            Interpreter.RegisterCommandHandler("lift", LiftItem); //LiftAction
            Interpreter.RegisterCommandHandler("lifttype", LiftType); //LiftTypeAction

            // Gump
            Interpreter.RegisterCommandHandler("waitforgump", WaitForGump); // WaitForGumpAction
            Interpreter.RegisterCommandHandler("gumpresponse", GumpResponse); // GumpResponseAction
            Interpreter.RegisterCommandHandler("gumpclose", GumpClose); // GumpResponseAction

            // Menu
            Interpreter.RegisterCommandHandler("menu", ContextMenu); //ContextMenuAction
            Interpreter.RegisterCommandHandler("contextmenu", ContextMenu); //ContextMenuAction
            Interpreter.RegisterCommandHandler("menuresponse", MenuResponse); //MenuResponseAction
            Interpreter.RegisterCommandHandler("waitformenu", WaitForMenu); //WaitForMenuAction

            // Prompt
            Interpreter.RegisterCommandHandler("promptresponse", PromptResponse); //PromptAction
            Interpreter.RegisterCommandHandler("waitforprompt", WaitForPrompt); //WaitForPromptAction
            Interpreter.RegisterCommandHandler("cancelprompt", CancelPrompt); //CancelPrompt added in from uosteam settings.

            // Hotkey execution
            Interpreter.RegisterCommandHandler("hotkey", Hotkey); //HotKeyAction Not implemented yet.


            // Messages executions
            Interpreter.RegisterCommandHandler("overhead", OverheadMessage); //OverheadMessageAction
            Interpreter.RegisterCommandHandler("headmsg", OverheadMessage); //OverheadMessageAction
            Interpreter.RegisterCommandHandler("sysmsg", SystemMessage); //SystemMessageAction
            Interpreter.RegisterCommandHandler("clearsysmsg", ClearSysMsg); //SystemMessageAction
            Interpreter.RegisterCommandHandler("clearjournal", ClearSysMsg); //SystemMessageAction

            // General Waits/Pauses
            Interpreter.RegisterCommandHandler("wait", Pause); //PauseAction
            Interpreter.RegisterCommandHandler("pause", Pause); //PauseAction
            Interpreter.RegisterCommandHandler("waitforsysmsg", WaitForSysMsg); //  Not implemented yet.
            Interpreter.RegisterCommandHandler("wfsysmsg", WaitForSysMsg); //  Not implemented yet.

            // Misc
            Interpreter.RegisterCommandHandler("setability", SetAbility); //SetAbilityAction
            Interpreter.RegisterCommandHandler("setlasttarget", SetLastTarget); //SetLastTargetAction
            Interpreter.RegisterCommandHandler("lasttarget", LastTarget); //LastTargetAction
            Interpreter.RegisterCommandHandler("skill", UseSkill); //SkillAction
            Interpreter.RegisterCommandHandler("useskill", UseSkill); //SkillAction
            Interpreter.RegisterCommandHandler("walk", Walk); //Move/WalkAction
            Interpreter.RegisterCommandHandler("potion", Potion);

            // Script related
            Interpreter.RegisterCommandHandler("script", PlayScript);
            Interpreter.RegisterCommandHandler("setvar", SetVar);
            Interpreter.RegisterCommandHandler("setvariable", SetVar);
            Interpreter.RegisterCommandHandler("unsetvar", UnsetVar);
            Interpreter.RegisterCommandHandler("unsetvariable", UnsetVar);

            Interpreter.RegisterCommandHandler("stop", Stop);
            Interpreter.RegisterCommandHandler("scriptstop", ScriptStop);
            
            Interpreter.RegisterCommandHandler("clearall", ClearAll);

            Interpreter.RegisterCommandHandler("clearhands", ClearHands);

            Interpreter.RegisterCommandHandler("virtue", Virtue);

            Interpreter.RegisterCommandHandler("random", Random);

            Interpreter.RegisterCommandHandler("cleardragdrop", ClearDragDrop);
            Interpreter.RegisterCommandHandler("interrupt", Interrupt);

            Interpreter.RegisterCommandHandler("sound", Sound);
            Interpreter.RegisterCommandHandler("playsound", Sound);
            Interpreter.RegisterCommandHandler("music", Music);
            Interpreter.RegisterCommandHandler("playmusic", Music);

            Interpreter.RegisterCommandHandler("classicuo", ClassicUOProfile);  //  Not implemented yet.
            Interpreter.RegisterCommandHandler("cuo", ClassicUOProfile);  //  Not implemented yet.

            Interpreter.RegisterCommandHandler("rename", Rename);

            Interpreter.RegisterCommandHandler("getlabel", GetLabel);  //  Not implemented yet.

            Interpreter.RegisterCommandHandler("ignore", AddIgnore);
            Interpreter.RegisterCommandHandler("ignoreobject", AddIgnore);
            Interpreter.RegisterCommandHandler("unignore", RemoveIgnore);
            Interpreter.RegisterCommandHandler("clearignore", ClearIgnore);

            Interpreter.RegisterCommandHandler("cooldown", Cooldown); //  Not implemented yet.

            Interpreter.RegisterCommandHandler("poplist", PopList);
            Interpreter.RegisterCommandHandler("pushlist", PushList);
            Interpreter.RegisterCommandHandler("removelist", RemoveList);
            Interpreter.RegisterCommandHandler("createlist", CreateList);
            Interpreter.RegisterCommandHandler("clearlist", ClearList);

            Interpreter.RegisterCommandHandler("settimer", SetTimer);
            Interpreter.RegisterCommandHandler("removetimer", RemoveTimer);
            Interpreter.RegisterCommandHandler("createtimer", CreateTimer);

            #endregion

            #region uosteam stuff

            // Commands. From RCEteam Documentation
            Interpreter.RegisterCommandHandler("fly", FlyCommand);
            Interpreter.RegisterCommandHandler("land", LandCommand);
            Interpreter.RegisterCommandHandler("clickobject", ClickObject);
            Interpreter.RegisterCommandHandler("bandageself", BandageSelf);
            Interpreter.RegisterCommandHandler("useonce", UseOnce);
            Interpreter.RegisterCommandHandler("clearusequeue", CleanUseQueue);
            Interpreter.RegisterCommandHandler("moveitem", MoveItem);
            Interpreter.RegisterCommandHandler("moveitemoffset", MoveItemOffset);
            Interpreter.RegisterCommandHandler("movetypeoffset", MoveTypeOffset);
            Interpreter.RegisterCommandHandler("turn", Turn);
            Interpreter.RegisterCommandHandler("pathfindto", PathFindTo);
            Interpreter.RegisterCommandHandler("run", Run);
            Interpreter.RegisterCommandHandler("feed", Feed);
            Interpreter.RegisterCommandHandler("shownames", ShowNames);
            Interpreter.RegisterCommandHandler("togglehands", ToggleHands);
            Interpreter.RegisterCommandHandler("equipitem", EquipItem);
            Interpreter.RegisterCommandHandler("togglemounted", ToggleMounted);
            Interpreter.RegisterCommandHandler("equipwand", EquipWand); //TODO: This method is a stub. Remove after successful testing.
            Interpreter.RegisterCommandHandler("buy", Buy);
            Interpreter.RegisterCommandHandler("sell", Sell);
            Interpreter.RegisterCommandHandler("clearbuy", ClearBuy);
            Interpreter.RegisterCommandHandler("location", Location);
            Interpreter.RegisterCommandHandler("clearsell", ClearSell);
            Interpreter.RegisterCommandHandler("organizer", Organizer);
            Interpreter.RegisterCommandHandler("restock", Restock);
            Interpreter.RegisterCommandHandler("autoloot", Autoloot); //TODO: This method is a stub. Remove after successful testing.
            Interpreter.RegisterCommandHandler("autotargetobject", AutoTargetObject);
            Interpreter.RegisterCommandHandler("dressconfig", DressConfig); // I can't tell what this is intended to do in RCE
            Interpreter.RegisterCommandHandler("toggleautoloot", ToggleAutoloot);
            Interpreter.RegisterCommandHandler("togglescavenger", ToggleScavenger);
            Interpreter.RegisterCommandHandler("counter", Counter); //This has no meaning in RE
            Interpreter.RegisterCommandHandler("waitforjournal", WaitForJournal);
            Interpreter.RegisterCommandHandler("info", Info);
            Interpreter.RegisterCommandHandler("ping", Ping);
            Interpreter.RegisterCommandHandler("resync", Resync);
            Interpreter.RegisterCommandHandler("snapshot", Snapshot);
            Interpreter.RegisterCommandHandler("where", Where);
            Interpreter.RegisterCommandHandler("messagebox", MessageBox);
            Interpreter.RegisterCommandHandler("mapuo", MapUO); // not going to implement
            Interpreter.RegisterCommandHandler("clickscreen", ClickScreen);
            Interpreter.RegisterCommandHandler("paperdoll", Paperdoll);
            Interpreter.RegisterCommandHandler("helpbutton", HelpButton); //not going to implement
            Interpreter.RegisterCommandHandler("guildbutton", GuildButton);
            Interpreter.RegisterCommandHandler("questsbutton", QuestsButton);
            Interpreter.RegisterCommandHandler("logoutbutton", LogoutButton);
            Interpreter.RegisterCommandHandler("virtue", Virtue);
            Interpreter.RegisterCommandHandler("msg", MsgCommand);
            Interpreter.RegisterCommandHandler("partymsg", PartyMsg);
            Interpreter.RegisterCommandHandler("guildmsg", GuildMsg);
            Interpreter.RegisterCommandHandler("allymsg", AllyMsg);
            Interpreter.RegisterCommandHandler("whispermsg", WhisperMsg);
            Interpreter.RegisterCommandHandler("yellmsg", YellMsg);
            Interpreter.RegisterCommandHandler("chatmsg", ChatMsg);
            Interpreter.RegisterCommandHandler("emotemsg", EmoteMsg);
            Interpreter.RegisterCommandHandler("timermsg", TimerMsg);

            Interpreter.RegisterCommandHandler("addfriend", AddFriend); //not so much
            Interpreter.RegisterCommandHandler("removefriend", RemoveFriend); // not implemented, use the gui


            Interpreter.RegisterCommandHandler("setskill", SetSkill);
            Interpreter.RegisterCommandHandler("waitforproperties", WaitForProperties);
            Interpreter.RegisterCommandHandler("autocolorpick", AutoColorPick);
            Interpreter.RegisterCommandHandler("waitforcontents", WaitForContents);
            Interpreter.RegisterCommandHandler("miniheal", MiniHeal);
            Interpreter.RegisterCommandHandler("bigheal", BigHeal);
            Interpreter.RegisterCommandHandler("chivalryheal", ChivalryHeal);
            Interpreter.RegisterCommandHandler("waitfortarget", WaitForTarget);
            Interpreter.RegisterCommandHandler("canceltarget", CancelTarget);
            Interpreter.RegisterCommandHandler("cancelautotarget", CancelAutoTarget);
            Interpreter.RegisterCommandHandler("target", Target);
            Interpreter.RegisterCommandHandler("targettype", TargetType);
            Interpreter.RegisterCommandHandler("targetground", TargetGround);
            Interpreter.RegisterCommandHandler("targettile", TargetTile);
            Interpreter.RegisterCommandHandler("targettileoffset", TargetTileOffset);
            Interpreter.RegisterCommandHandler("targettilerelative", TargetTileRelative);
            Interpreter.RegisterCommandHandler("targetresource", TargetResource);
            Interpreter.RegisterCommandHandler("cleartargetqueue", ClearTargetQueue);
            Interpreter.RegisterCommandHandler("warmode", WarMode);
            Interpreter.RegisterCommandHandler("getenemy", GetEnemy); //TODO: add "transformations" list
            Interpreter.RegisterCommandHandler("getfriend", GetFriend); //TODO: add "transformations" list
            Interpreter.RegisterCommandHandler("namespace", ManageNamespaces); //TODO: add "transformations" list
            Interpreter.RegisterCommandHandler("script", ManageScripts); //TODO: add "transformations" list

            #endregion


            #region Expressions

            Interpreter.RegisterExpressionHandler("str", (ASTNode node, Argument[] args, bool quiet) => Player.Str);
            Interpreter.RegisterExpressionHandler("int", (ASTNode node, Argument[] args, bool quiet) => Player.Int);
            Interpreter.RegisterExpressionHandler("dex", (ASTNode node, Argument[] args, bool quiet) => Player.Dex);

            Interpreter.RegisterExpressionHandler("stam", (ASTNode node, Argument[] args, bool quiet) => Player.Stam);
            Interpreter.RegisterExpressionHandler("maxstam", (ASTNode node, Argument[] args, bool quiet) => Player.StamMax);
            Interpreter.RegisterExpressionHandler("diffstam", (ASTNode node, Argument[] args, bool quiet) => (int)(Player.StamMax - Player.Stam));

            Interpreter.RegisterExpressionHandler("hp", (ASTNode node, Argument[] args, bool quiet) => Player.Hits);
            Interpreter.RegisterExpressionHandler("maxhp", (ASTNode node, Argument[] args, bool quiet) => Player.HitsMax);
            Interpreter.RegisterExpressionHandler("diffhp", (ASTNode node, Argument[] args, bool quiet) => (int)(Player.HitsMax - Player.Hits));

            Interpreter.RegisterExpressionHandler("hits", (ASTNode node, Argument[] args, bool quiet) => Player.Hits);
            Interpreter.RegisterExpressionHandler("maxhits", (ASTNode node, Argument[] args, bool quiet) => Player.HitsMax);
            Interpreter.RegisterExpressionHandler("diffhits", (ASTNode node, Argument[] args, bool quiet) => (int)(Player.HitsMax - Player.Hits));

            Interpreter.RegisterExpressionHandler("mana", (ASTNode node, Argument[] args, bool quiet) => Player.Mana);
            Interpreter.RegisterExpressionHandler("maxmana", (ASTNode node, Argument[] args, bool quiet) => Player.ManaMax);
            Interpreter.RegisterExpressionHandler("diffmana", (ASTNode node, Argument[] args, bool quiet) => (int)(Player.ManaMax - Player.Mana));

            Interpreter.RegisterExpressionHandler("poisoned", (ASTNode node, Argument[] args, bool quiet) => Player.Poisoned);

            Interpreter.RegisterExpressionHandler("hidden", (ASTNode node, Argument[] args, bool quiet) => !Player.Visible);

            Interpreter.RegisterExpressionHandler("mounted", (ASTNode node, Argument[] args, bool quiet) => Player.Mount != null);

            Interpreter.RegisterExpressionHandler("rhandempty", (ASTNode node, Argument[] args, bool quiet) => Player.GetItemOnLayer("RightHand") == null);
            Interpreter.RegisterExpressionHandler("lhandempty", (ASTNode node, Argument[] args, bool quiet) => Player.GetItemOnLayer("LeftHand") == null);

            Interpreter.RegisterExpressionHandler("dead", (ASTNode node, Argument[] args, bool quiet) => Player.IsGhost);

            Interpreter.RegisterExpressionHandler("weight", (ASTNode node, Argument[] args, bool quiet) => Player.Weight);
            Interpreter.RegisterExpressionHandler("maxweight", (ASTNode node, Argument[] args, bool quiet) => Player.MaxWeight);
            Interpreter.RegisterExpressionHandler("diffweight", (ASTNode node, Argument[] args, bool quiet) => (int)(Player.MaxWeight - Player.Weight));

            Interpreter.RegisterExpressionHandler("skill", Skill);
            Interpreter.RegisterExpressionHandler("position", Position);

            Interpreter.RegisterExpressionHandler("followers", (ASTNode node, Argument[] args, bool quiet) => Player.Followers);
            Interpreter.RegisterExpressionHandler("maxfollowers", (ASTNode node, Argument[] args, bool quiet) => Player.FollowersMax);
            Interpreter.RegisterExpressionHandler("name", (ASTNode node, Argument[] args, bool quiet) => Player.Name);

            Interpreter.RegisterExpressionHandler("targetexists", TargetExists);

            Interpreter.RegisterExpressionHandler("paralyzed", (ASTNode node, Argument[] args, bool quiet) => Player.Paralized);

            Interpreter.RegisterExpressionHandler("invuln", (ASTNode node, Argument[] args, bool quiet) => Player.YellowHits);
            Interpreter.RegisterExpressionHandler("invul", (ASTNode node, Argument[] args, bool quiet) => Player.YellowHits);
            Interpreter.RegisterExpressionHandler("blessed", (ASTNode node, Argument[] args, bool quiet) => Player.YellowHits);

            Interpreter.RegisterExpressionHandler("warmode", (ASTNode node, Argument[] args, bool quiet) => Player.WarMode);

            Interpreter.RegisterExpressionHandler("count", CountExpression);  // not implemented
            Interpreter.RegisterExpressionHandler("counter", CountExpression); // not implemented

            Interpreter.RegisterExpressionHandler("insysmsg", InSysMessage); // not implemented
            Interpreter.RegisterExpressionHandler("insysmessage", InSysMessage); // not implemented

            Interpreter.RegisterExpressionHandler("findtype", FindType);

            Interpreter.RegisterExpressionHandler("findbuff", FindBuffDebuff);
            Interpreter.RegisterExpressionHandler("finddebuff", FindBuffDebuff);

            Interpreter.RegisterExpressionHandler("queued", (ASTNode node, Argument[] args, bool quiet) => ActionQueue.Empty);

            Interpreter.RegisterExpressionHandler("varexist", VarExist);
            Interpreter.RegisterExpressionHandler("varexists", VarExist);
        
            Interpreter.RegisterExpressionHandler("itemcount", (ASTNode node, Argument[] args, bool quiet) => Assistant.World.Player.Backpack.GetTotalCount()); //Player.Backpack.GetTotalCount()

            Interpreter.RegisterExpressionHandler("poplist", PopListExp);
            Interpreter.RegisterExpressionHandler("listexists", ListExists);
            Interpreter.RegisterExpressionHandler("list", ListLength);
            Interpreter.RegisterExpressionHandler("inlist", InList);
            
            Interpreter.RegisterExpressionHandler("timer", TimerValue);
            Interpreter.RegisterExpressionHandler("timerexists", TimerExists);

            #endregion



            #region uosteam expressions

            // Expressions





            m_Interpreter.RegisterExpressionHandler("movetype", MoveType);

            m_Interpreter.RegisterExpressionHandler("findalias", FindAlias);
            m_Interpreter.RegisterExpressionHandler("x", LocationX);
            m_Interpreter.RegisterExpressionHandler("y", LocationY);
            m_Interpreter.RegisterExpressionHandler("z", LocationZ);
            m_Interpreter.RegisterExpressionHandler("organizing", Organizing);
            m_Interpreter.RegisterExpressionHandler("restocking", Restocking);

            m_Interpreter.RegisterExpressionHandler("contents", CountContents);
            m_Interpreter.RegisterExpressionHandler("inregion", InRegion);
            m_Interpreter.RegisterExpressionHandler("skill", Skill);
            m_Interpreter.RegisterExpressionHandler("skillvalue", Skill);
            m_Interpreter.RegisterExpressionHandler("skillbase", SkillBase);
            m_Interpreter.RegisterExpressionHandler("findobject", FindObject);
            m_Interpreter.RegisterExpressionHandler("amount", Amount);

            m_Interpreter.RegisterExpressionHandler("distance", Distance);
            m_Interpreter.RegisterExpressionHandler("graphic", Graphic);
            m_Interpreter.RegisterExpressionHandler("inrange", InRange);
            m_Interpreter.RegisterExpressionHandler("buffexists", BuffExists);
            m_Interpreter.RegisterExpressionHandler("property", Property);
            m_Interpreter.RegisterExpressionHandler("durability", Durability);
            m_Interpreter.RegisterExpressionHandler("findtype", FindType);
            m_Interpreter.RegisterExpressionHandler("findlayer", FindLayer);
            m_Interpreter.RegisterExpressionHandler("skillstate", SkillState);
            m_Interpreter.RegisterExpressionHandler("counttype", CountType);
            m_Interpreter.RegisterExpressionHandler("counttypeground", CountTypeGround);
            m_Interpreter.RegisterExpressionHandler("findwand", FindWand); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterExpressionHandler("inparty", InParty); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterExpressionHandler("infriendlist", InFriendList);
            m_Interpreter.RegisterExpressionHandler("ingump", InGump);
            m_Interpreter.RegisterExpressionHandler("gumpexists", GumpExists);
            m_Interpreter.RegisterExpressionHandler("injournal", InJournal);
            m_Interpreter.RegisterExpressionHandler("listexists", ListExists);
            m_Interpreter.RegisterExpressionHandler("list", ListCount);
            m_Interpreter.RegisterExpressionHandler("inlist", InList);
            m_Interpreter.RegisterExpressionHandler("timer", Timer);
            m_Interpreter.RegisterExpressionHandler("timerexists", TimerExists);
            m_Interpreter.RegisterExpressionHandler("targetexists", TargetExists);
            m_Interpreter.RegisterExpressionHandler("serial", SerialOf);



            // Player Attributes
            m_Interpreter.RegisterExpressionHandler("physical", (ASTNode node, Argument[] args, bool quiet) => Player.AR);
            m_Interpreter.RegisterExpressionHandler("fire", (ASTNode node, Argument[] args, bool quiet) => Player.FireResistance);
            m_Interpreter.RegisterExpressionHandler("cold", (ASTNode node, Argument[] args, bool quiet) => Player.ColdResistance);
            m_Interpreter.RegisterExpressionHandler("poison", (ASTNode node, Argument[] args, bool quiet) => Player.PoisonResistance);
            m_Interpreter.RegisterExpressionHandler("energy", (ASTNode node, Argument[] args, bool quiet) => Player.EnergyResistance);

            m_Interpreter.RegisterExpressionHandler("gold", (ASTNode node, Argument[] args, bool quiet) => Player.Gold);
            m_Interpreter.RegisterExpressionHandler("luck", (ASTNode node, Argument[] args, bool quiet) => Player.Luck);
            m_Interpreter.RegisterExpressionHandler("waitingfortarget", WaitingForTarget); //TODO: loose approximation, see inside

            m_Interpreter.RegisterExpressionHandler("name", Name);
            m_Interpreter.RegisterExpressionHandler("dead", IsDead);
            m_Interpreter.RegisterExpressionHandler("direction", Direction); //Dalamar: Fix original RCE direction, in numbers
            m_Interpreter.RegisterExpressionHandler("directionname", DirectionName);  //Dalamar: Added RE style directions with names
            m_Interpreter.RegisterExpressionHandler("flying", IsFlying);
            m_Interpreter.RegisterExpressionHandler("paralyzed", IsParalyzed);

            m_Interpreter.RegisterExpressionHandler("criminal", IsCriminal);
            m_Interpreter.RegisterExpressionHandler("enemy", IsEnemy);
            m_Interpreter.RegisterExpressionHandler("friend", IsFriend);
            m_Interpreter.RegisterExpressionHandler("gray", IsGray);
            m_Interpreter.RegisterExpressionHandler("innocent", IsInnocent);
            m_Interpreter.RegisterExpressionHandler("murderer", IsMurderer);

            m_Interpreter.RegisterExpressionHandler("bandage", Bandage);
            m_Interpreter.RegisterExpressionHandler("color", Color);
            #endregion
            // Object attributes
        }

        #region Dummy and Placeholders

        private static IComparable ExpressionNotImplemented(ASTNode node, Argument[] args, bool _)
        {
            string expression = node.Lexeme;
            Utility.Logger.Info("Expression Not Implemented {0} {1}", expression, args);
            return 0;
        }

        private static bool NotImplemented(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string command = node.Lexeme;
            Utility.Logger.Info("RCE: NotImplemented {0} {1}", command, args);
            return true;
        }


        #endregion

        #region Expressions

        /// <summary>
        /// skill ('name of skill')
        /// </summary>
        private IComparable Skill(ASTNode node, Argument[] vars, bool quiet)
        {
            if (vars.Length < 1)
                throw new RCERuntimeError(node, "Usage: skill ('name of skill')");
            bool force = false;

            if (vars.Length == 2)
                force = vars[2].AsBool();

            if (World.Player == null)
                return 0;

            string skillname = vars[0].AsString();
            double skillvalue = Player.GetSkillValue(skillname);

            if (force)
                skillvalue = Player.GetRealSkillValue(skillname);

            return skillvalue;
        }

        /// <summary>
        /// position (x, y) or position (x, y, z)
        /// </summary>
        private IComparable Position(ASTNode node, Argument[] vars, bool quiet)
        {
            if (World.Player == null)
                return false;

            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: position (x, y) or position (x, y, z)");
            }

            int x = vars[0].AsInt();
            int y = vars[1].AsInt();
            int z = (vars.Length > 2)
                ? vars[2].AsInt()
                : World.Player.Position.Z;

            return World.Player.Position.X == x
                && World.Player.Position.Y == y
                && World.Player.Position.Z == z;
        }

        /// <summary>
        ///  targetexists (0 = neutral, 1 = harmful, 2 = beneficial, 3 = any)
        /// </summary>
        private static IComparable TargetExists(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length >= 1)
            {
                CultureInfo cultureInfo = Thread.CurrentThread.CurrentCulture;
                TextInfo textInfo = cultureInfo.TextInfo;
                string targetFlag = textInfo.ToTitleCase(args[0].AsString().ToLower());
                return RazorEnhanced.Target.HasTarget(targetFlag);
            }
            return RazorEnhanced.Target.HasTarget();

        }

        /// <summary>
        ///  count ('name of counter') OR count ('name of item' OR graphicID) [hue]
        /// </summary>
        private static IComparable CountExpression(ASTNode node, Argument[] vars, bool quiet)
        {
            return NotImplemented(node, vars, quiet, false);
            /*
            if (vars.Length < 1)
                throw new RCERuntimeError(node, "Usage: count ('name of counter') OR count ('name of item' OR graphicID) [hue]");

            if (World.Player == null)
                return 0;

            var counter = Counter.FindCounter(vars[0].AsString());
            if (counter != null)
            {
                return counter.Amount;
            }

            string gfxStr = vars[0].AsString();
            Serial gfx = Utility.ToUInt16(gfxStr, 0);
            ushort hue = 0xFFFF;

            if (vars.Length == 2)
            {
                hue = Utility.ToUInt16(vars[1].AsString(), 0xFFFF);
            }

            // No graphic id, maybe searching by name?
            if (gfx == 0)
            {
                var items = CommandHelper.GetItemsByName(gfxStr, true, false, -1);

                return items.Count == 0 ? 0 : Counter.GetCount(items[0].ItemID, hue);
            }

            return Counter.GetCount(new ItemID((ushort)gfx.Value), hue);

            */
        }

        /// <summary>
        ///  insysmsg ('text')
        /// </summary>
        private static IComparable InSysMessage(ASTNode node, Argument[] vars, bool quiet)
        {
            return NotImplemented(node, vars, quiet, false);

            /*
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: insysmsg ('text')");
            }

            string text = vars[0].AsString();

            return SystemMessages.Exists(text);
            */
        }

        /// <summary>
        /// findtype ('name of item'/'graphicID) [inrangecheck (true/false)/backpack] [hue]
        /// </summary>
        private static IComparable FindType(ASTNode node, Argument[] vars, bool quiet)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: findtype ('name of item'/'graphicID) [inrangecheck (true/false)/backpack] [hue]");
            }

            string gfxStr = vars[0].AsString();
            Serial gfx = Utility.ToUInt16(gfxStr, 0);
            List<Item> items;
            List<Mobile> mobiles;

            bool inRangeCheck = false;
            bool backpack = false;
            int hue = -1;

            if (vars.Length > 1)
            {
                if (vars.Length == 3)
                {
                    hue = vars[2].AsInt();
                }

                if (vars[1].AsString().IndexOf("pack", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    backpack = true;
                }
                else
                {
                    inRangeCheck = vars[1].AsBool();
                }
            }

            // No graphic id, maybe searching by name?
            if (gfx == 0)
            {
                items = CommandHelper.GetItemsByName(gfxStr, backpack, inRangeCheck, hue);

                if (items.Count == 0) // no item found, search mobile by name
                {
                    mobiles = CommandHelper.GetMobilesByName(gfxStr, inRangeCheck);

                    if (mobiles.Count > 0)
                    {
                        return mobiles[Utility.Random(mobiles.Count)].Serial;
                    }
                }
                else
                {
                    return items[Utility.Random(items.Count)].Serial;
                }
            }
            else // Provided graphic id for type, check backpack first (same behavior as DoubleClickAction in macros
            {
                ushort id = Utility.ToUInt16(gfxStr, 0);

                items = CommandHelper.GetItemsById(id, backpack, inRangeCheck, hue);

                // Still no item? Mobile check!
                if (items.Count == 0)
                {
                    mobiles = CommandHelper.GetMobilesById(id, inRangeCheck);

                    if (mobiles.Count > 0)
                    {
                        return mobiles[Utility.Random(mobiles.Count)].Serial;
                    }
                }
                else
                {
                    return items[Utility.Random(items.Count)].Serial;
                }
            }

            return Serial.Zero;
        }


        /// <summary>
        /// findbuff/finddebuff ('name of buff')
        /// </summary>
        private static IComparable FindBuffDebuff(ASTNode node, Argument[] vars, bool quiet)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: findbuff/finddebuff ('name of buff')");
            }

            string name = vars[0].AsString();

            foreach (BuffDebuff buff in World.Player.BuffsDebuffs)
            {
                if (buff.ClilocMessage1.IndexOf(name, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// varexist ('name')
        /// </summary>
        private IComparable VarExist(ASTNode node, Argument[] vars, bool quiet)
        {
            if (vars.Length != 1)
            {
                throw new RCERuntimeError(node, "Usage: varexist ('name')");
            }

            if (vars.Length == 1)
            {
                string alias = vars[0].AsString();
                return Interpreter.FindAlias(alias);
            }


            return false;
        }

        /// <summary>
        /// poplist ('list name') ('element value'/'front'/'back')
        /// </summary>
        private static IComparable PopListExp(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length != 2)
                throw new RCERuntimeError(node, "Usage: poplist ('list name') ('element value'/'front'/'back')");

            var listName = args[0].AsString();
            var frontBackOrElementVar = args[1];
            var isFrontOrBack = frontBackOrElementVar.AsString() == "front" || frontBackOrElementVar.AsString() == "back";

            if (isFrontOrBack)
            {
                var isFront = frontBackOrElementVar.AsString() == "front";

                Interpreter.PopList(listName, isFront, out var popped);
                return popped.AsSerial();
            }

            var evaluatedVar = new Variable(frontBackOrElementVar.AsString());

            if (force)
            {
                while (Interpreter.PopList(listName, evaluatedVar)) { }
                return Serial.Zero;
            }

            Interpreter.PopList(listName, evaluatedVar);
            return evaluatedVar.AsSerial();
        }










        /// <summary>
        /// if contents (serial) ('operator') ('value')
        /// </summary>
        private static IComparable CountContents(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Item container = Items.FindBySerial((int)serial);
                if (container != null)
                {
                    if (!container.IsContainer)
                        return 0;

                    Items.WaitForProps(container, 1000);

                    string content = Items.GetPropValueString(container.Serial, "contents");
                    if (string.IsNullOrEmpty(content))
                    {
                        List<Item> list = container.Contains;
                        return list.Count;
                    }

                    var contents = content.Split('/');

                    return Utility.ToInt32(contents[0], 0);
                }
                return 0;
            }

            return false;
        }

        /// <summary>
        /// counttype (graphic) (color) (source) (operator) (value)
        /// </summary>
        private static IComparable CountType(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 3)
            {
                int graphic = args[0].AsInt();
                int color = args[1].AsInt();
                uint container = args[2].AsSerial();
                int count = Items.ContainerCount((int)container, graphic, color, true);
                return count;
            }

            return false;
        }

        /// <summary>
        /// if injournal ('text') ['author'/'system']
        /// </summary>
        private IComparable InJournal(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string text = args[0].AsString();
                return m_journal.Search(text);
            }
            if (args.Length == 2)
            {
                string text = args[0].AsString();
                string texttype = args[1].AsString();
                texttype = texttype.Substring(0, 1).ToUpper() + texttype.Substring(1).ToLower();  // syStEm -> System
                return m_journal.SearchByType(text, texttype);
            }


            return false;
        }

        /// <summary>
        /// if listexists ('list name')
        /// </summary>
        private IComparable ListExists(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string list = args[0].AsString();
                return m_Interpreter.ListExists(list);
            }

            return false;
        }


        /// <summary>
        /// findalias ('alias name')
        /// </summary>
        private IComparable FindAlias(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                return m_Interpreter.FindAlias(alias);
            }

            return false;
        }

        /// <summary>
        /// x (serial)
        /// </summary>
        private static IComparable LocationX(ASTNode node, Argument[] args, bool quiet)
        {
            uint serial = (uint)Player.Serial;
            if (args.Length >= 1)
            {
                serial = args[0].AsSerial();
            }
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.X;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.X;
                }
            }

            throw new RCERuntimeError(node, "X location serial not found");
        }

        /// <summary>
        /// y (serial)
        /// </summary>
        private static IComparable LocationY(ASTNode node, Argument[] args, bool quiet)
        {
            uint serial = (uint)Player.Serial;
            if (args.Length >= 1)
            {
                serial = args[0].AsSerial();
            }
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Y;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Y;
                }
            }

            throw new RCERuntimeError(node, "Y location serial not found");

        }

        /// <summary>
        /// z (serial)
        /// </summary>
        private static IComparable LocationZ(ASTNode node, Argument[] args, bool quiet)
        {
            uint serial = (uint)Player.Serial;
            if (args.Length >= 1)
            {
                serial = args[0].AsSerial();
            }
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Z;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Z;
                }
            }

            throw new RCERuntimeError(node, "Z location serial not found");
            // return 0;
        }


        /// <summary>
        /// if not organizing
        /// </summary>
        private static IComparable Organizing(ASTNode node, Argument[] args, bool quiet)
        {
            return RazorEnhanced.Organizer.Status();
        }

        /// <summary>
        /// if not restock
        /// </summary>
        private static IComparable Restocking(ASTNode node, Argument[] args, bool quiet)
        {
            return RazorEnhanced.Restock.Status();
        }



        /// The problem is RCE findbyid will find either mobil or item, but RE seperates them
        /// So this function will look for both and return the list
        ///   if it is an item it can't be a mobile and vica-versa
        private static List<int> FindByType_ground(int graphic, int color, int amount, int range)
        {
            List<int> retList = new List<int>();
            // Search for items first
            Items.Filter itemFilter = new Items.Filter
            {
                Enabled = true,
                CheckIgnoreObject = true
            };
            itemFilter.Graphics.Add(graphic);
            itemFilter.RangeMax = range;
            itemFilter.OnGround = 1;

            if (color != -1)
                itemFilter.Hues.Add(color);
            List<Item> items = RazorEnhanced.Items.ApplyFilter(itemFilter);

            if (items.Count > 0)
            {
                foreach (var i in items)
                {
                    if ((amount <= 0) || (i.Amount >= amount))
                        retList.Add(i.Serial);
                }
                //return retList;
            }

            Mobiles.Filter mobileFilter = new Mobiles.Filter
            {
                Enabled = true,
                CheckIgnoreObject = true
            };
            mobileFilter.Bodies.Add(graphic);
            mobileFilter.RangeMax = range;
            if (color != -1)
                mobileFilter.Hues.Add(color);
            List<Mobile> mobiles = RazorEnhanced.Mobiles.ApplyFilter(mobileFilter);

            if (mobiles.Count > 0)
            {
                foreach (var m in mobiles)
                {
                    retList.Add(m.Serial);
                }
                //return retList;
            }

            return retList;
        }

        internal static int CountType_ground(int graphic, int color, int range)
        {
            int retCount = 0;

            // Search for items first
            Items.Filter itemFilter = new Items.Filter
            {
                Enabled = true,
                CheckIgnoreObject = true
            };
            itemFilter.Graphics.Add(graphic);
            itemFilter.RangeMax = range;
            itemFilter.OnGround = 1;
            if (color != -1)
                itemFilter.Hues.Add(color);
            List<Item> items = RazorEnhanced.Items.ApplyFilter(itemFilter);

            if (items.Count > 0)
            {
                foreach (var i in items)
                {
                    retCount += i.Amount;
                }
            }

            Mobiles.Filter mobileFilter = new Mobiles.Filter
            {
                CheckIgnoreObject = true,
                Enabled = true
            };
            mobileFilter.Bodies.Add(graphic);
            mobileFilter.RangeMax = range;
            if (color != -1)
                mobileFilter.Hues.Add(color);
            List<Mobile> mobiles = RazorEnhanced.Mobiles.ApplyFilter(mobileFilter);

            if (mobiles.Count > 0)
            {
                foreach (var m in mobiles)
                {
                    retCount += 1;
                }
            }

            return retCount;
        }

        /// <summary>
        /// findtype (graphic) [color] [source] [amount] [range or search level]
        /// </summary>
        private IComparable FindTypes(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "FindType requires parameters");
                // return false;
            }
            m_Interpreter.UnSetAlias("found");
            int serial = -1;

            int color = -1;
            if (args.Length > 1)
                color = args[1].AsInt();

            bool groundCheck = false;
            int container = Player.Backpack.Serial;  // default if not specified
            if (args.Length > 2)
            {
                groundCheck = (args[2].AsString().ToLower() == "ground");
                container = args[2].AsInt();
            }

            // Intresting note from RCEteam, amount there cant exceed 15356
            // also amount is a single entity with that amount, not a sum
            int amount = -1;
            if (args.Length > 3)
                amount = args[3].AsInt();

            int range = 100;
            if (args.Length > 4)
                range = args[4].AsInt();


            string listname = args[0].AsString();
            if (m_Interpreter.ListExists(listname))
            {
                IronPython.Runtime.PythonList itemids = new IronPython.Runtime.PythonList();
                foreach (Argument arg in m_Interpreter.ListContents(listname))
                {
                    ushort type = arg.AsUShort();
                    if (groundCheck)
                        serial = FindByTypeOnGround(type, color, amount, range);
                    else
                        serial = FindByType(type, color, container, amount, range);
                    if (serial != -1)
                        break;
                }
            }
            else
            {
                ushort type = args[0].AsUShort();
                if (groundCheck)
                    serial = FindByTypeOnGround(type, color, amount, range);
                else
                    serial = FindByType(type, color, container, amount, range);
            }

            if (serial != -1)
            {
                m_Interpreter.SetAlias("found", (uint)serial);
                return true;
            }

            return false;
        }

        internal int FindByType(ushort type, int color, int container, int amount, int depth)
        {
            var items = Items.FindAllByID(type, color, container, depth, true);
            foreach (var pyThing in items)
            {
                RazorEnhanced.Item item = (RazorEnhanced.Item)pyThing;
                if (amount == -1 || item.Amount >= amount)
                {
                    return item.Serial;
                }
            }
            return -1; // none found
        }

        internal int FindByTypeOnGround(ushort type, int color, int amount, int range)
        {
            var results = World.FindAllEntityByID(type, color);
            foreach (var entity in results)
            {
                if (Misc.CheckIgnoreObject(entity.Serial))
                    continue;

                if (Player.DistanceTo(entity) > range)
                    continue;

                if (entity.Serial.IsItem)
                {
                    var item = (Assistant.Item)entity;
                    if (!item.OnGround)
                        continue;

                    if (amount == -1 || item.Amount >= amount)
                    {
                        return item.Serial;
                    }
                }

                if (entity.Serial.IsMobile)
                {
                    Assistant.Mobile mobile = (Assistant.Mobile)entity;
                    if (mobile != null)
                    {
                        // assume all mobiles are on ground
                        // assume all mobiles are Amount 1 so ignore amount
                        return mobile.Serial;
                    }
                }
            }
            return -1;
        }


        /// <summary>
        /// property ('name') (serial) [operator] [value]
        /// </summary>
        private static IComparable Property(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RCERuntimeError(node, "Property requires 2 parameters");
                // return false;
            }

            string findProp = args[0].AsString().ToLower();
            uint serial = args[1].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    Items.WaitForProps(thing, 1000);
                    float value = Items.GetPropValue(item, findProp);
                    return value;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    Mobiles.WaitForProps(thing, 1000);
                    foreach (var prop in item.Properties)
                    {
                        var lowerProp = prop.ToString().ToLower();
                        if (lowerProp.Contains(findProp))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// durability ('name') (serial) [operator] [value]
        /// </summary>
        private static IComparable Durability(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "durability requires 1 parameters");
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Durability;
                }
            }

            return false;
        }


        /// <summary>
        /// ingump (gump id/'any') ('text')
        /// </summary>
        private static IComparable InGump(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 2)
            {
                uint gumpid = args[0].AsSerial();
                string serach_text = args[1].AsString().ToLower();
                uint curGumpid = Gumps.CurrentGump();

                if (gumpid == 0xffffffff || gumpid == curGumpid)
                {
                    var gump_text = String.Join("\n", Gumps.LastGumpGetLineList()).ToLower();
                    return gump_text.Contains(serach_text);

                }

            }
            return false;
        }

        /// <summary>
        /// war (serial)
        /// </summary>
        private static IComparable InWarMode(ASTNode node, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.WarMode;
                }
            }
            return false;
        }


        /// <summary>
        /// name [serial]
        /// </summary>
        private static IComparable Name(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Name;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Name;
                }
            }

            return false;
        }

        /// <summary>
        /// color  (operator) (value)
        /// </summary>
        private static IComparable Color(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Color requires parameters");
                // return false;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsMobile)
            {
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                {
                    int color = mobile.Color;
                    return color;
                }
                return false;
            }

            Item item = Items.FindBySerial((int)serial);
            if (item == null)
                return false;

            int hue = item.Hue;
            return hue;
        }

        /// <summary>
        /// dead [serial]
        /// </summary>
        private static IComparable IsDead(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.IsGhost;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.IsGhost;
                }
            }

            return false;
        }

        /// <summary>
        /// directionname [serial]
        /// </summary>
        private static IComparable DirectionName(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Direction;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Direction;
                }
            }

            return false;
        }

        /// <summary>
        /// direction [serial]
        /// </summary>
        private static IComparable Direction(ASTNode node, Argument[] args, bool quiet)
        {
            //RCE Direction -  Start in top-right-corner: 0 | North. Inclements: clockwise
            Dictionary<string, int> dir_num = new Dictionary<string, int>() {
                {"North",0}, {"Right",1}, {"East",2}, {"Down",3},
                {"South",4}, {"Left",5},  {"West",6}, {"Up",7},


            };

            string direction = null;
            if (args.Length == 0)
            {
                direction = Player.Direction;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    direction = theMobile.Direction;
                }
            }

            if (dir_num.ContainsKey(direction))
            {
                return dir_num[Player.Direction];
            }

            return false;
        }

        /// <summary>
        /// flying [serial]
        /// </summary>
        private static IComparable IsFlying(ASTNode node, Argument[] args, bool quiet)
        {
            uint serial = (uint)Player.Serial;
            if (args.Length >= 1)
            {
                serial = args[0].AsSerial();
            }
            Mobile theMobile = Mobiles.FindBySerial((int)serial);
            if (theMobile != null)
            {
                return theMobile.Flying;
            }
            return false;
        }

        /// <summary>
        /// paralyzed [serial]
        /// </summary>
        private static IComparable IsParalyzed(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Paralized;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Paralized;
            }
            return false;
        }

        /// <summary>
        /// yellowhits [serial]
        /// </summary>
        private static IComparable YellowHits(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.YellowHits;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.YellowHits;
            }
            return false;
        }
        /*
         // hue color #30
            0x000000, // black      unused 0
            0x30d0e0, // blue       0x0059 1
            0x60e000, // green      0x003F 2
            0x9090b2, // greyish    0x03b2 3
            0x909090, // grey          "   4
            0xd88038, // orange     0x0090 5
            0xb01000, // red        0x0022 6
            0xe0e000, // yellow     0x0035 7
        */

        /// <summary>
        /// criminal [serial]
        /// </summary>
        private static IComparable IsCriminal(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 4;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 4;
            }
            return false;
        }

        /// <summary>
        /// murderer [serial]
        /// </summary>
        private static IComparable IsMurderer(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 6;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 6;
            }
            return false;
        }

        /// <summary>
        /// enemy serial
        /// </summary>
        private static IComparable IsEnemy(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "enemy requires parameters");
                // return false;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.IsHuman && (theMobile.Notoriety >= 4 && theMobile.Notoriety <= 6);
            }
            return false;
        }

        /// <summary>
        /// friend serial
        /// </summary>
        private static IComparable IsFriend(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "friend requires parameters");
            }

            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.IsHuman && theMobile.Notoriety < 4;
            }
            return false;
        }

        /// <summary>
        /// gray serial
        /// </summary>
        private static IComparable IsGray(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 3;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 3;
            }
            return false;
        }

        /// <summary>
        /// innocent serial
        /// </summary>
        private static IComparable IsInnocent(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 2;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 2;
            }
            return false;
        }

        /// <summary>
        /// bandage
        /// </summary>
        private static IComparable Bandage(ASTNode node, Argument[] args, bool quiet)
        {
            int count = Items.ContainerCount((int)Player.Backpack.Serial, 0x0E21, -1, true);
            if (count > 0 && (Player.Hits < Player.HitsMax || Player.Poisoned))
                BandageHeal.Heal(Assistant.World.Player, false);
            return count;
        }

        /// <summary>
        /// gumpexists (gump id/'any')
        /// </summary>
        private static IComparable GumpExists(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Gumps.HasGump();
            }

            if (args.Length == 1)
            {
                uint gumpid = args[0].AsUInt();
                return Gumps.HasGump(gumpid);
            }
            return -1;
        }

        /// <summary>
        /// list ('list name') (operator) (value)
        /// </summary>
        private IComparable ListCount(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                string listName = args[0].AsString();
                return m_Interpreter.ListLength(listName);
            }

            WrongParameterCount(node, 1, args.Length, "list command requires 1 parameter, the list name");
            return 0;
        }

        /// <summary>
        /// inlist ('list name') ('element value')
        /// </summary>
        private IComparable InList(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 2)
            {
                string listName = args[0].AsString();
                return m_Interpreter.ListContains(listName, args[1]);  // This doesn't seem right
            }
            return 0;
        }

        /// <summary>
        /// serial ('target')
        /// returns the serial of the target be it mobile or item
        /// </summary>
        /// 
        private static IComparable SerialOf(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                uint target = args[0].AsSerial();
                return target;
            }
            return 0;
        }


        /// <summary>
        /// skillstate ('skill name') (operator) ('locked'/'up'/'down')
        /// </summary>
        private static IComparable SkillState(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                string skill = args[0].AsString();
                int value = Player.GetSkillStatus(skill);
                switch (value)
                {
                    case 0:
                        return "up";
                    case 1:
                        return "down";
                    case 2:
                        return "locked";
                }
            }
            return "unknown";
        }

        /// <summary>
        /// inregion ('guards'/'town'/'dungeon'/'forest') [serial] [range]
        /// </summary>
        private static IComparable InRegion(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "inregion requires parameters");
                // return false;
            }

            string desiredRegion = args[0].AsString();
            string region = Player.Zone();
            if (args.Length == 1)
            {
                return desiredRegion.ToLower() == region.ToLower();
            }
            if (args.Length == 3)
            {
                uint serial = args[1].AsSerial();
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile == null)
                    return false;
                int range = args[2].AsInt();
                ConfigFiles.RegionByArea.Area area = Player.Area(Player.Map, mobile.Position.X, mobile.Position.Y);
                if (area == null)
                    return false;
                if (desiredRegion.ToLower() != area.zoneName.ToLower())
                    return false;

                foreach (System.Drawing.Rectangle rect in area.rect)
                {
                    if (rect.Contains(mobile.Position.X, mobile.Position.Y))
                    {
                        System.Drawing.Rectangle desiredRect = new System.Drawing.Rectangle(rect.X - range, rect.Y - range, rect.Width - range, rect.Height - range);
                        if (desiredRect.Contains(mobile.Position.X, mobile.Position.Y))
                            return true;
                    }
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// findwand NOT IMPLEMENTED
        /// </summary>
        private static IComparable FindWand(ASTNode node, Argument[] args, bool quiet)
        {
            return ExpressionNotImplemented(node, args, quiet);
        }

        /// <summary>
        /// inparty NOT IMPLEMENTED
        /// </summary>
        private static IComparable InParty(ASTNode node, Argument[] args, bool quiet)
        {
            return ExpressionNotImplemented(node, args, quiet);
        }

        /// <summary>
        /// skillbase ('name') (operator) (value)
        /// </summary>
        private static IComparable SkillBase(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Skill requires parameters");
                // return false;
            }

            string skillname = args[0].AsString();
            double skillvalue = Player.GetRealSkillValue(skillname);
            return skillvalue;
        }




        /// <summary>
        /// amount (serial)
        /// </summary>
        private IComparable Amount(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Amount require a parameter");
                // return false;
            }
            uint serial = args[0].AsSerial();
            Item item = Items.FindBySerial((int)serial);
            if (item == null)
                return false;

            return item.Amount;
        }

        /// <summary>
        /// findobject (serial) [color] [source] [amount] [range]
        /// </summary>
        private IComparable FindObject(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Find Object requires parameters");
                // return false;
            }
            m_Interpreter.UnSetAlias("found");
            int color = -1;
            int amount = -1;
            int container = -1;
            int range = -1;

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (args.Length >= 2)
            {
                color = args[1].AsInt();
            }
            if (args.Length >= 3)
            {
                container = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                amount = args[3].AsInt();
            }
            if (args.Length >= 5)
            {
                range = args[4].AsInt();
            }

            if (thing.IsMobile)
            {
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                {
                    if (range != -1)
                        if (Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, mobile.Position.X, mobile.Position.Y) > range)
                            return false;

                    if (color == -1 || color == mobile.Hue)
                    {
                        m_Interpreter.SetAlias("found", (uint)mobile.Serial);
                        return true;
                    }
                }
                return false;
            }

            // must be an item
            Item item = Items.FindBySerial((int)serial);
            if (item == null)
                return false;

            // written this way because I hate if ! && !
            if (color != -1)
                if (item.Hue != color)
                    return false;

            if (container != -1)
                if (item.Container != container)
                    return false;

            if (amount != -1)
                if (item.Amount < amount)
                    return false;

            if (range != -1)
                if (Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, item.Position.X, item.Position.Y) > range)
                    return false;
            m_Interpreter.SetAlias("found", (uint)item.Serial);
            return true;
        }

        /// <summary>
        /// graphic (serial) (operator) (value)
        /// </summary>
        private static IComparable Graphic(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length > 1)
            {
                throw new RCERuntimeError(node, "graphic Object requires 0 or 1 parameters");
            }

            if (args.Length == 0)
                return Player.MobileID;

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                return item.ItemID;
            }
            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                return item.MobileID;
            }

            return Int32.MaxValue;
        }


        /// <summary>
        /// distance (serial) (operator) (value)
        /// </summary>
        private static IComparable Distance(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Distance Object requires parameters");
                // return Int32.MaxValue;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            int x = -1;
            int y = -1;
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                x = item.Position.X;
                y = item.Position.Y;
            }
            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                x = item.Position.X;
                y = item.Position.Y;
            }

            if (x == -1 || y == -1)
                return Int32.MaxValue;

            return Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, x, y);
        }

        /// <summary>
        /// inrange (serial) (range)
        /// </summary>
        private static IComparable InRange(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RCERuntimeError(node, "Find Object requires parameters");
                // return false;
            }
            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            int range = args[1].AsInt();
            int x = -1;
            int y = -1;

            if (thing.IsMobile)
            {
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                {
                    x = mobile.Position.X;
                    y = mobile.Position.Y;
                }
            }
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    x = item.Position.X;
                    y = item.Position.Y;
                }
            }

            if (x == -1 || y == -1)
                return false;

            int distance = Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, x, y);
            return (distance <= range);
        }

        /// <summary>
        /// buffexists ('buff name')
        /// </summary>
        private static IComparable BuffExists(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length >= 1)
            {
                return Player.BuffsExist(args[0].AsString());
            }

            return false;

        }

        /// <summary>
        /// findlayer (serial) (layer)
        /// </summary>
        private IComparable FindLayer(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RCERuntimeError(node, "Find Object requires parameters");
                // return false;
            }
            uint serial = args[0].AsSerial();
            Assistant.Mobile mobile = Assistant.World.FindMobile((Assistant.Serial)((uint)serial));
            if (mobile == null)
                return false;

            Assistant.Layer layer = (Assistant.Layer)args[1].AsInt();
            Assistant.Item item = mobile.GetItemOnLayer(layer);
            if (item != null)
            {
                m_Interpreter.SetAlias("found", (uint)item.Serial);
                return true;
            }
            return false;
        }

        /// <summary>
        /// counttypeground (graphic) (color) (range) (operator) (value)
        /// </summary>
        private static IComparable CountTypeGround(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "CountTypeGround requires parameters");
                // return 0;
            }

            int graphic = args[0].AsInt();
            int color = -1;
            int range = -1;
            //
            if (args.Length > 1)
            {
                color = args[1].AsInt();
            }
            if (args.Length > 2)
            {
                range = args[2].AsInt();
            }

            int count = CountType_ground(graphic, color, range);

            return count;
        }

        /// <summary>
        /// infriendlist (serial)
        /// </summary>
        private static IComparable InFriendList(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length > 0)
            {
                uint serial = args[0].AsSerial();
                Friend.FriendPlayer player = new Friend.FriendPlayer("FAKE", (int)serial, true);
                string selection = Friend.FriendListName;
                if (RazorEnhanced.Settings.Friend.ListExists(selection))
                {
                    if (RazorEnhanced.Settings.Friend.PlayerExists(selection, player))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        ///  timer ('timer name') (operator) (value)
        /// </summary>
        private IComparable Timer(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1) { WrongParameterCount(node, 1, args.Length); }

            return m_Interpreter.GetTimer(args[0].AsString()).TotalMilliseconds;

        }

        /// <summary>
        ///  timerexists ('timer name')
        /// </summary>
        private IComparable TimerExists(ASTNode node, Argument[] args, bool quiet)
        {
            if (args.Length < 1) { WrongParameterCount(node, 1, args.Length); }
            return m_Interpreter.TimerExists(args[0].AsString());
        }



        /// <summary>
        ///  waitingfortarget POORLY IMPLEMENTED
        /// </summary>
        private static IComparable WaitingForTarget(ASTNode node, Argument[] args, bool quiet)
        {
            //TODO: This is an very loose approximation. Waitingfortarget should know if there is any "pending target" coming from the server.
            //RCE Tester: Lermster#2355
            return RazorEnhanced.Target.HasTarget();
        }




        #endregion

        #region Commands

        /// <summary>
        /// attack (serial)
        /// </summary>
        private static bool Attack(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: attack (serial)");
            }

            uint serial = args[0].AsSerial();
            Mobile mobile = Mobiles.FindBySerial((int)serial);
            Item item = Items.FindBySerial((int)serial);
            if (mobile != null || item != null)
                Player.Attack((int)serial);

            return true;
        }

        /// <summary>
        /// cast (spell id/'spell name'/'last') [serial]
        /// </summary>
        private bool Cast(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string spell = args[0].AsString();
                Spells.Cast(spell);
            }
            else if (args.Length == 2)
            {
                string spell = args[0].AsString();
                uint serial = args[1].AsSerial();
                Spells.Cast(spell, serial);
            }
            else
            {
                SendError("Usage: cast (spell id/'spell name'/'last') [serial]");
                return false;
            }

            return true;
        }

        /// <summary>
        /// dress ['profile name']
        /// </summary>
        private bool Dress(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string dressListName = args[0].AsString();
                if (Settings.Dress.ListExists(dressListName))
                {
                    RazorEnhanced.Dress.ChangeList(dressListName);
                }
                else
                {
                    SendError(String.Format("Dress List {0} does not exist", dressListName));
                }
            }
            RazorEnhanced.Dress.DressFStart();
            while (RazorEnhanced.Dress.DressStatus())
            {
                Misc.Pause(100);
            }
            return true;
        }

        /// <summary>
        /// undress ['profile name']
        /// </summary>
        private bool Undress(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string unDressListName = args[0].AsString();
                if (Settings.Dress.ListExists(unDressListName))
                {
                    RazorEnhanced.Dress.ChangeList(unDressListName);
                }
                else
                {
                    SendError(String.Format("UnDress List {0} does not exist", unDressListName));
                }
            }
            RazorEnhanced.Dress.UnDressFStart();
            while (RazorEnhanced.Dress.UnDressStatus())
            {
                Misc.Pause(100);
            }
            return true;
        }

        /// <summary>
        /// usetype ('name of item'/'graphicID') [inrangecheck (true/false)/backpack] [hue]
        /// </summary>
        private bool UseType(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: usetype ('name of item'/'graphicID') [inrangecheck (true/false)/backpack] [hue]");
            }

            string gfxStr = vars[0].AsString();
            Serial gfx = Utility.ToInt32(gfxStr, 0);
            List<Assistant.Item> items;
            List<Assistant.Mobile> mobiles = new List<Assistant.Mobile>();

            bool inRangeCheck = false;
            bool backpack = false;
            int hue = -1;

            if (vars.Length > 1)
            {
                if (vars.Length == 3)
                {
                    hue = vars[2].AsInt();
                }

                if (vars[1].AsString().IndexOf("pack", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    backpack = true;
                }
                else
                {
                    inRangeCheck = vars[1].AsBool();
                }
            }

            // No graphic id, maybe searching by name?
            if (gfx == 0)
            {
                items = Assistant.Item.GetItemsByName(gfxStr, backpack, inRangeCheck, hue);

                if (items.Count == 0) // no item found, search mobile by name
                {
                    mobiles = Mobiles.GetMobilesByName(gfxStr, inRangeCheck);
                }
            }
            else // Provided graphic id for type, check backpack first (same behavior as DoubleClickAction in macros
            {
                ushort id = Utility.ToUInt16(gfxStr, 0);

                items = Assistant.Item.GetItemsById(id, backpack, inRangeCheck, hue);

                // Still no item? Mobile check!
                if (items.Count == 0)
                {
                    mobiles = Mobiles.GetMobilesById(id, inRangeCheck);
                }
            }

            if (items.Count > 0)
            {
                PlayerData.DoubleClick(items[Utility.Random(items.Count)].Serial);
            }
            else if (mobiles.Count > 0)
            {
                PlayerData.DoubleClick(mobiles[Utility.Random(mobiles.Count)].Serial);
            }
            else
            {
                if (!quiet)
                    SendError($"Item or mobile type '{gfxStr}' not found");
            }

            return true;
        }

        /// <summary>
        /// dclicktype ('name of item'/'graphicID') [inrangecheck (true/false)/backpack] [hue]
        /// </summary>
        private bool DClickType(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: dclicktype ('name of item'/'graphicID') [inrangecheck (true/false)/backpack] [hue]");
            }

            string gfxStr = vars[0].AsString();
            Serial gfx = Utility.ToInt32(gfxStr, 0);
            List<Assistant.Item> items;
            List<Assistant.Mobile> mobiles = new List<Assistant.Mobile>();

            bool inRangeCheck = false;
            bool backpack = false;
            int hue = -1;

            if (vars.Length > 1)
            {
                if (vars.Length == 3)
                {
                    hue = vars[2].AsInt();
                }

                if (vars[1].AsString().IndexOf("pack", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    backpack = true;
                }
                else
                {
                    inRangeCheck = vars[1].AsBool();
                }
            }

            // No graphic id, maybe searching by name?
            if (gfx == 0)
            {
                items = Assistant.Item.GetItemsByName(gfxStr, backpack, inRangeCheck, hue);

                if (items.Count == 0) // no item found, search mobile by name
                {
                    mobiles = Mobiles.GetMobilesByName(gfxStr, inRangeCheck);
                }
            }
            else // Provided graphic id for type, check backpack first (same behavior as DoubleClickAction in macros
            {
                ushort id = Utility.ToUInt16(gfxStr, 0);

                items = Assistant.Item.GetItemsById(id, backpack, inRangeCheck, hue);

                // Still no item? Mobile check!
                if (items.Count == 0)
                {
                    mobiles = Mobiles.GetMobilesById(id, inRangeCheck);
                }
            }

            if (items.Count > 0)
            {
                PlayerData.DoubleClick(items[Utility.Random(items.Count)].Serial);
            }
            else if (mobiles.Count > 0)
            {
                PlayerData.DoubleClick(mobiles[Utility.Random(mobiles.Count)].Serial);
            }
            else
            {
                if (!quiet)
                    SendError($"Item or mobile type '{gfxStr}' not found");
            }

            return true;
        }

        /// <summary>
        /// useobject (serial)
        /// </summary>
        private static bool UseObject(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: useobject (serial) or dclick ('left'/'right'/'hands')");
            }

            Item item;



            switch (vars[0].AsString())
            {
                case "left":
                    item = Player.GetItemOnLayer("LeftHand");
                    break;
                case "right":
                    item = Player.GetItemOnLayer("RightHand");
                    break;
                default:
                    item = Player.GetItemOnLayer("RightHand") ?? Player.GetItemOnLayer("LeftHand");
                    break;
            }

            if (item != null)
            {
                PlayerData.DoubleClick(item);
            }
            else
            {
                Serial serial = vars[0].AsSerial();

                if (!serial.IsValid)
                {
                    throw new RCERuntimeError(node, "useobject - invalid serial");
                }

                PlayerData.DoubleClick(serial);
            }

            return true;
        }

        /// <summary>
        /// dclick (serial) or dclick ('left'/'right'/'hands')
        /// </summary>
        private static bool DClick(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: dclick (serial) or dclick ('left'/'right'/'hands')");
            }

            Item item;



            switch (vars[0].AsString())
            {
                case "left":
                    item = Player.GetItemOnLayer("LeftHand");
                    break;
                case "right":
                    item = Player.GetItemOnLayer("RightHand");
                    break;
                default:
                    item = Player.GetItemOnLayer("RightHand") ?? Player.GetItemOnLayer("LeftHand");
                    break;
            }

            if (item != null)
            {
                PlayerData.DoubleClick(item);
            }
            else
            {
                Serial serial = vars[0].AsSerial();

                if (!serial.IsValid)
                {
                    throw new RCERuntimeError(node, "dclick - invalid serial");
                }

                PlayerData.DoubleClick(serial);
            }

            return true;
        }

        /// <summary>
        /// drop (serial) (x y z/layername)
        /// </summary>
        private bool DropItem(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: drop (serial) (x y z/layername)");
            }

            Assistant.Serial serial = vars[0].AsString().IndexOf("ground", StringComparison.OrdinalIgnoreCase) > 0
                ? uint.MaxValue
                : vars[0].AsSerial();

            Assistant.Point3D to = new Assistant.Point3D(0, 0, 0);
            Layer layer = Layer.Invalid;

            switch (vars.Length)
            {
                case 1: // drop at feet if only serial is provided
                    to = new Assistant.Point3D(World.Player.Position.X, World.Player.Position.Y, World.Player.Position.Z);
                    break;
                case 2: // dropping on a layer
                    layer = (Layer)Enum.Parse(typeof(Layer), vars[1].AsString(), true);
                    break;
                case 3: // x y
                    to = new Assistant.Point3D(Utility.ToInt32(vars[1].AsString(), 0), Utility.ToInt32(vars[2].AsString(), 0), 0);
                    break;
                case 4: // x y z
                    to = new Assistant.Point3D(Utility.ToInt32(vars[1].AsString(), 0), Utility.ToInt32(vars[2].AsString(), 0),
                        Utility.ToInt32(vars[3].AsString(), 0));
                    break;
            }

            if (Assistant.DragDropManager.Holding != null)
            {
                if (layer > Layer.Invalid && layer <= Layer.LastUserValid)
                {
                    Assistant.Mobile m = World.FindMobile(serial);
                    if (m != null)
                        Assistant.DragDropManager.Drop(Assistant.DragDropManager.Holding, m, layer);
                }
                else
                {
                    Assistant.DragDropManager.Drop(Assistant.DragDropManager.Holding, serial, to);
                }
            }
            else
            {
                SendError("Not holding anything");
                
            }

            return true;
        }

        /// <summary>
        /// droprelloc (x) (y)
        /// </summary>
        private bool DropRelLoc(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: droprelloc (x) (y)");
            }

            int x = vars[0].AsInt();
            int y = vars[1].AsInt();

            if (Assistant.DragDropManager.Holding != null)
            {
                Assistant.DragDropManager.Drop(Assistant.DragDropManager.Holding, null,
                    new Assistant.Point3D((ushort)(World.Player.Position.X + x),
                        (ushort)(World.Player.Position.Y + y), World.Player.Position.Z));
            }
            else
            {
                SendError("Not holding anything");
            }

            return true;
        }

        private static int _lastLiftId;
        /// <summary>
        /// lift (serial) [amount] [timeout]
        /// </summary>
        private bool LiftItem(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: lift (serial) [amount] [timeout]");
            }

            Serial serial = vars[0].AsSerial();

            if (!serial.IsValid)
            {
                throw new RCERuntimeError(node, "lift - Invalid serial");
            }

            ushort amount = 1;

            if (vars.Length == 2)
            {
                amount = Utility.ToUInt16(vars[1].AsString(), 1);
            }

            long timeout = 30000;

            if (vars.Length == 3)
            {
                timeout = Utility.ToLong(vars[2].AsString(), 30000);
            }

            if (_lastLiftId > 0)
            {
                if (Assistant.DragDropManager.LastIDLifted == _lastLiftId)
                {
                    _lastLiftId = 0;
                    Interpreter.ClearTimeout();
                    return true;
                }

                Interpreter.Timeout(timeout, () =>
                {
                    _lastLiftId = 0;
                    return true;
                });
            }
            else
            {
                Assistant.Item item = World.FindItem(serial);

                if (item != null)
                {
                    _lastLiftId = Assistant.DragDropManager.Drag(item, amount <= item.Amount ? amount : item.Amount);
                }
                else
                {
                    SendError("Item not found or out of range");
                    return true;
                }
            }

            return false;
        }

        private static int _lastLiftTypeId;
        /// <summary>
        /// lifttype (gfx/'name of item') [amount] [hue]
        /// </summary>
        private bool LiftType(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: lifttype (gfx/'name of item') [amount] [hue]");
            }

            string gfxStr = vars[0].AsString();
            ushort gfx = Utility.ToUInt16(gfxStr, 0);
            ushort amount = 1;
            int hue = -1;

            if (vars.Length > 1)
            {
                if (vars.Length >= 2)
                {
                    amount = Utility.ToUInt16(vars[1].AsString(), 1);
                }

                if (vars.Length == 3)
                {
                    hue = Utility.ToUInt16(vars[2].AsString(), 0);
                }
            }

            if (_lastLiftTypeId > 0)
            {
                if (Assistant.DragDropManager.LastIDLifted == _lastLiftTypeId)
                {
                    _lastLiftTypeId = 0;
                    Interpreter.ClearTimeout();
                    return true;
                }

                Interpreter.Timeout(30000, () =>
                {
                    _lastLiftTypeId = 0;
                    return true;
                });
            }
            else
            {
                List<Assistant.Item> items = new List<Assistant.Item>();

                // No graphic id, maybe searching by name?
                if (gfx == 0)
                {
                    items = World.Player.Backpack.FindItemsByName(gfxStr, true);

                    if (items.Count == 0)
                    {
                        SendError($"Item '{gfxStr}' not found");
                        return true;
                    }
                }
                else
                {
                    items = World.Player.Backpack.FindItemsById(gfx, true);
                }

                if (hue > -1)
                {
                    items.RemoveAll(item => item.Hue != hue);
                }

                if (items.Count > 0)
                {
                    Assistant.Item item = items[Utility.Random(items.Count)];

                    if (item.Amount < amount)
                        amount = item.Amount;

                    _lastLiftTypeId = Assistant.DragDropManager.Drag(item, amount);
                }
                else
                {
                    SendError(Language.Format(LocString.NoItemOfType, (TypeID)gfx));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// waitforgump (gumpId/'any') [timeout]
        /// </summary>
        private bool WaitForGump(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: waitforgump (gumpId/'any') [timeout]");
            }

            uint gumpId = 0;
            bool strict = false;

            if (vars[0].AsString().IndexOf("any", StringComparison.OrdinalIgnoreCase) != -1)
            {
                strict = false;
            }
            else
            {
                gumpId = Utility.ToUInt32(vars[0].AsString(), 0);

                if (gumpId > 0)
                {
                    strict = true;
                }
            }

            Interpreter.Timeout(vars.Length == 2 ? vars[1].AsUInt() : 30000, () => { return true; });

            if ((World.Player.HasGump ) &&
                (World.Player.CurrentGumpI == gumpId || !strict || gumpId == 0))
            {
                Interpreter.ClearTimeout();
                return true;
            }

            return false;
        }

        /// <summary>
        /// gumpresponse (buttondId) [switch ...]
        /// </summary>
        private bool GumpResponse(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: gumpresponse (buttondId) [switch ...]");
            }

            if (args.Length == 2)
            {
                uint gumpid = args[0].AsUInt();
                int buttonid = args[1].AsInt();
                Gumps.SendAction(gumpid, buttonid);
                return true;
            }
            if (args.Length > 2)
            {
                uint gumpid = args[0].AsUInt();
                int buttonid = args[1].AsInt();
                IronPython.Runtime.PythonList switches = new IronPython.Runtime.PythonList();
                IronPython.Runtime.PythonList textIds = new IronPython.Runtime.PythonList();
                IronPython.Runtime.PythonList textValues = new IronPython.Runtime.PythonList();

                string sl = args[2].AsString().Trim().Replace("\"", "");
                if (sl.Length > 0)
                {
                    List<int> switchList = sl.Split(',').Select(int.Parse).ToList();

                    foreach (int sw in switchList)
                    {
                        switches.Add(sw);
                    }
                }
                if (args.Length > 4)
                {
                    string til = args[3].AsString().Trim().Replace("\"", "");
                    if (til.Length > 0)
                    {
                        List<int> textIdList = til.Split(',').Select(int.Parse).ToList();
                        foreach (int textId in textIdList)
                        {
                            textIds.Add(textId);
                        }
                    }
                    string tv = args[4].AsString().Trim().Replace("\"", "");
                    if (tv.Length > 0)
                    {
                        List<string> textValueList = tv.Split(',').ToList();
                        foreach (string textValue in textValueList)
                        {
                            textValues.Add(textValue);
                        }
                    }
                }

                Gumps.SendAdvancedAction(gumpid, buttonid, switches, textIds, textValues);
                return true;
            }

            return true;
        }
        
        /// <summary>
        /// gumpclose (gumpID)
        /// </summary>
        private bool GumpClose(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            uint gumpI = World.Player.CurrentGumpI;

            if (vars.Length > 0)
            {
                gumpI = vars[0].AsUInt();
            }


            if (!Gumps.HasGump(gumpI))
            {
                SendError($"'{gumpI}' unknown gump id");
                return true;
            }

            Gumps.CloseGump(gumpI);

            return true;
        }

        /// <summary>
        /// menu (serial) (index)
        /// </summary>
        private static bool ContextMenu(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: menu (serial) (index)");
            }

            Serial s = vars[0].AsSerial();
            ushort index = vars[1].AsUShort();
            bool blockPopup = true;

            if (vars.Length > 2)
            {
                blockPopup = vars[2].AsBool();
            }

            if (s == Serial.Zero && World.Player != null)
                s = World.Player.Serial;

            //ScriptManager.BlockPopupMenu = blockPopup;  // Not currently implemented.

            Assistant.Client.Instance.SendToServer(new ContextMenuRequest(s));
            Assistant.Client.Instance.SendToServer(new ContextMenuResponse(s, index));
            return true;
        }

        /// <summary>
        /// menuresponse (index) (menuId) [hue]
        /// </summary>
        private static bool MenuResponse(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: menuresponse (index) (menuId) [hue]");
            }

            ushort index = vars[0].AsUShort();
            ushort menuId = vars[1].AsUShort();
            ushort hue = 0;

            if (vars.Length == 3)
                hue = vars[2].AsUShort();

            Assistant.Client.Instance.SendToServer(new MenuResponse(World.Player.CurrentMenuS, World.Player.CurrentMenuI, index,
                menuId, hue));
            World.Player.HasMenu = false;
            return true;
        }

        /// <summary>
        /// waitformenu (menuId/'any') [timeout]
        /// </summary>
        private bool WaitForMenu(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: waitformenu (menuId/'any') [timeout]");
            }

            uint menuId = 0;

            // Look for a specific menu
            menuId = vars[0].AsString().IndexOf("any", StringComparison.OrdinalIgnoreCase) != -1
                ? 0
                : Utility.ToUInt32(vars[0].AsString(), 0);

            Interpreter.Timeout(vars.Length == 2 ? vars[1].AsUInt() : 30000, () => { return true; });

            if (World.Player.HasMenu && (World.Player.CurrentGumpI == menuId || menuId == 0))
            {
                Interpreter.ClearTimeout();
                return true;
            }

            return false;
        }

        /// <summary>
        /// promptresponse ('response to the prompt')
        /// </summary>
        private static bool PromptResponse(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: promptresponse ('response to the prompt')");
            }

            Misc.ResponsePrompt(vars[0].AsString());
            return true;
        }

        /// <summary>
        /// waitforprompt (promptId/'any') [timeout]
        /// </summary>
        private bool WaitForPrompt(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: waitforprompt (promptId/'any') [timeout]");
            }

            uint promptId = 0;
            bool strict = false;

            // Look for a specific prompt
            if (vars[0].AsString().IndexOf("any", StringComparison.OrdinalIgnoreCase) != -1)
            {
                strict = false;
            }
            else
            {
                promptId = Utility.ToUInt32(vars[0].AsString(), 0);

                if (promptId > 0)
                {
                    strict = true;
                }
            }

            Interpreter.Timeout(vars.Length == 2 ? vars[1].AsUInt() : 30000, () => { return true; });

            if (World.Player.HasPrompt && (World.Player.PromptID == promptId || !strict || promptId == 0))
            {
                Interpreter.ClearTimeout();
                return true;
            }

            return false;
        }

        /// <summary>
        /// cancelprompt 
        /// </summary>
        private static bool CancelPrompt(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Misc.CancelPrompt();
            return true;
        }

        /// <summary>
        /// hotkey ('name of hotkey') OR (hotkeyId)
        /// </summary>
        private static bool Hotkey(ASTNode node, Argument[] vars, bool quiet, bool force)
        {

            return NotImplemented(node, vars, quiet, force);
            /*
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: hotkey ('name of hotkey') OR (hotkeyId)");
            }

            string query = vars[0].AsString();

            KeyData hk = HotKey.GetByNameOrId(query);

            if (hk == null)
            {
                throw new RunTimeError($"{command} - Hotkey '{query}' not found");
            }

            hk.Callback();

            return true;

            */
        }

        /// <summary>
        /// overhead ('text') [color] [serial]
        /// </summary>
        private static bool OverheadMessage(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: overhead ('text') [color] [serial]");
            }

            string msg = vars[0].AsString();
            // msg = CommandHelper.ReplaceStringInterpolations(msg);  not implemented

            int color = 0;
            int mobile = Player.Serial;
            if (vars.Length == 2)
            {
                int value = (int)vars[1].AsSerial();
                if (value < 10240)
                    color = value;
                else
                    mobile = value;
            }
            if (vars.Length == 3)
            {
                color = vars[1].AsInt();
                mobile = (int)vars[2].AsSerial();
            }

            Mobiles.Message(mobile, color, msg);

            return true;
        }

        /// <summary>
        /// sysmsg ('text') [color]
        /// </summary>
        private static bool SystemMessage(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: sysmsg ('text') [color]");
            }


            if (vars.Length == 1)
            {
                Misc.SendMessage(vars[0].AsString());
            }
            else if (vars.Length == 2)
            {
                Misc.SendMessage(vars[0].AsString(), vars[1].AsInt(), false);
            }
            else
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// sysmsg ('text') [color]
        /// </summary>
        private bool ClearSysMsg(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            m_journal.Clear();
            return true;
        }

        /// <summary>
        /// wait (timeout) [shorthand]
        /// </summary>
        private bool Pause(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
                throw new RCERuntimeError(node, "Usage: wait (timeout) [shorthand]");

            uint timeout = vars[0].AsUInt();

            if (vars.Length == 2)
            {
                switch (vars[1].AsString())
                {
                    case "s":
                    case "sec":
                    case "secs":
                    case "second":
                    case "seconds":
                        timeout *= 1000;
                        break;
                    case "m":
                    case "min":
                    case "mins":
                    case "minute":
                    case "minutes":
                        timeout *= 60000;
                        break;
                }
            }

            Interpreter.Pause(timeout);

            return true;
        }

        /// <summary>
        /// waitforsysmsg 'message to wait for' [timeout]
        /// </summary>
        private bool WaitForSysMsg(ASTNode node, Argument[] vars, bool quiet, bool force)
        {

            return NotImplemented(node, vars, quiet, force);

            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: waitforsysmsg 'message to wait for' [timeout]");
            }
            /*
            if (SystemMessages.Exists(vars[0].AsString()))
            {
                Interpreter.ClearTimeout();
                return true;
            }
            */
            Interpreter.Timeout(vars.Length > 1 ? vars[1].AsUInt() : 30000, () => { return true; });

            return false;
        }

        private static readonly string[] Abilities = { "primary", "secondary", "stun", "disarm" };

        /// <summary>
        /// setability ('primary'/'secondary'/'stun'/'disarm') ['on'/'off']
        /// </summary>
        private static bool SetAbility(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1 || !Abilities.Contains(vars[0].AsString()))
            {
                throw new RCERuntimeError(node, "Usage: setability ('primary'/'secondary'/'stun'/'disarm') ['on'/'off']");
            }

            if (vars.Length == 2 && vars[1].AsString() == "on" || vars.Length == 1)
            {
                switch (vars[0].AsString())
                {
                    case "primary":
                        Player.WeaponPrimarySA();
                        break;
                    case "secondary":
                        Player.WeaponSecondarySA();
                        break;
                    case "stun":
                        Player.WeaponStunSA();
                        break;
                    case "disarm":
                        Player.WeaponDisarmSA();
                        break;
                }
            }
            else if (vars.Length == 2 && vars[1].AsString() == "off")
            {
                Assistant.Client.Instance.SendToServer(new UseAbility(AOSAbility.Clear));
                Assistant.Client.Instance.SendToClient(ClearAbility.Instance);
            }

            return true;
        }

        /// <summary>
        /// setlasttarget ('serial')
        /// </summary>
        private static bool SetLastTarget(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: setlasttarget ('serial')");
            }

            Serial serial = vars[0].AsSerial();

            if (serial != Serial.Zero)
            {
                RazorEnhanced.Target.SetLast(serial);
            }

            return true;
        }

        /// <summary>
        /// setlasttarget ('serial')
        /// </summary>
        private static bool LastTarget(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (!Targeting.DoLastTarget())
                Targeting.ResendTarget();

            return true;
        }

        /// <summary>
        /// skill ('skill name'/'last')
        /// </summary>
        private bool UseSkill(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: skill ('skill name'/'last')");
            }

            int skillId = 0;

            if (World.Player.LastSkill != -1)
            {
                skillId = World.Player.LastSkill;
            }

            if (vars[0].AsString() == "last")
            {
                Assistant.Client.Instance.SendToServer(new UseSkill(World.Player.LastSkill));
            }
            else if (vars.Length == 1)
            {
                string skill = vars[0].AsString();
                Player.UseSkill(skill);
            }
            else if (vars.Length == 2)
            {
                string skill = vars[0].AsString();
                int serial = (int)vars[1].AsSerial();
                Player.UseSkill(skill, serial);
            }
           
            /*
            if (vars[0].AsString() == "Stealth" && !World.Player.Visible)
            {
                StealthSteps.Hide();
            } */

            return true;
        }

        /// <summary>
        /// walk ('direction')
        /// </summary>
        private static bool Walk(ASTNode node, Argument[] var, bool quiet, bool force)
        {

            if (var.Length == 0)
                Player.Walk(Player.Direction);

            if (var.Length == 1)
            {
                string direction = var[0].AsString().ToLower();
                if (!map.ContainsKey(direction))
                {
                    throw new RCEArgumentError(node, var[0].AsString() + " not recognized.");
                }
                direction = map[direction];
                if (Player.Direction != direction)
                    Player.Walk(direction);
                Player.Walk(direction);
            }

            /*
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: walk ('direction')");
            }

            if (ScriptManager.LastWalk + TimeSpan.FromSeconds(0.4) >= DateTime.UtcNow)
            {
                return false;
            }

            ScriptManager.LastWalk = DateTime.UtcNow;

            Direction dir = (Direction)Enum.Parse(typeof(Direction), vars[0].AsString(), true);
            Client.Instance.RequestMove(dir);
            */

            return true;
        }

        private static readonly Dictionary<string, ushort> PotionList = new Dictionary<string, ushort>()
        {
            {"heal", 3852},
            {"cure", 3847},
            {"refresh", 3851},
            {"nightsight", 3846},
            {"ns", 3846},
            {"explosion", 3853},
            {"strength", 3849},
            {"str", 3849},
            {"agility", 3848}
        };

        /// <summary>
        /// potion ('type')
        /// </summary>
        private bool Potion(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 0)
            {
                throw new RCERuntimeError(node, "Usage: potion ('type')");
            }

            Assistant.Item pack = World.Player.Backpack;
            if (pack == null)
                return true;

            if (PotionList.TryGetValue(vars[0].AsString().ToLower(), out ushort potionId))
            {
                if (potionId == 3852 && World.Player.Poisoned && RazorEnhanced.Settings.General.ReadBool("BlockHealPoison") &&
                    Assistant.Client.Instance.AllowBit(FeatureBit.BlockHealPoisoned))
                {
                    World.Player.SendMessage(MsgLevel.Force, LocString.HealPoisonBlocked);
                    return true;
                }

                if (!World.Player.UseItem(pack, potionId))
                {
                    SendError(Language.Format(LocString.NoItemOfType, (TypeID)potionId));
                }
            }
            else
            {
                throw new RCERuntimeError(node, $" Unknown potion type");
            }

            return true;
        }

        /// <summary>
        /// script 'name of script'
        /// </summary>
        private static bool PlayScript(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: script 'name of script'");
            }

            Misc.ScriptRun(vars[0].AsString());

            return true;
        }

        /// <summary>
        /// setvar ('variable') [serial] [timeout]
        /// </summary>
        private bool SetVar(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1 || vars.Length > 2)
            {
                throw new RCERuntimeError(node, "Usage: setvar ('variable') [serial] [timeout]");
            }

            if (vars.Length == 1)
            {
                return PromptAlias(node, vars, quiet, force);
            }
            if (vars.Length == 2)
            {
                string alias = vars[0].AsString();
                uint value = vars[1].AsSerial();
                m_Interpreter.SetAlias(alias, value);
            }

            return true;
        }

        /// <summary>
        /// unsetvar ('name')
        /// </summary>
        private bool UnsetVar(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: unsetvar ('name')");

            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                m_Interpreter.UnSetAlias(alias);
            }

            return true;
        }

        /// <summary>
        /// stop
        /// </summary>
        private static bool Stop(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            Misc.ScriptStopAll();

            return true;
        }

        /// <summary>
        /// scriptstop 'name of script'
        /// </summary>
        private static bool ScriptStop(ASTNode node, Argument[] vars, bool quiet, bool force)
        {

            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: scriptstop 'name of script'");
            }

            Misc.ScriptStop(vars[0].AsString());

            return true;
        }

        /// <summary>
        /// clearall
        /// </summary>
        private static bool ClearAll(ASTNode node, Argument[] vars, bool quiet, bool force)
        {

            Assistant.DragDropManager.Clear(); // clear drag/drop queue
            RazorEnhanced.Target.Cancel(); // clear target queue & cancel current target
            Assistant.DragDropManager.DropCurrent(); // drop what you are currently holding

            return true;
        }

        /// <summary>
        /// random 'max value'
        /// </summary>
        private static bool Random(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 1)
            {
                throw new RCERuntimeError(node, "Usage: random 'max value'");
            }

            int max = vars[0].AsInt();

            World.Player.SendMessage(MsgLevel.Info, $"Random: {Utility.Random(1, max)}");

            return true;
        }

        /// <summary>
        /// cleardragdrop
        /// </summary>
        private static bool ClearDragDrop(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            Assistant.DragDropManager.Clear(); // clear drag/drop queue

            return true;
        }

        /// <summary>
        /// interrupt 'layer'(optional)
        /// </summary>
        private static bool Interrupt(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length == 1)
            {
                Layer layer = (Layer)Enum.Parse(typeof(Layer), vars[0].AsString(), true);

                if (layer > Layer.Invalid && layer <= Layer.LastUserValid)
                {
                    //Spells.Interrupt(layer);

                    Assistant.Item item = World.Player.GetItemOnLayer(layer);

                    if (item != null)
                    {
                        Assistant.Client.Instance.SendToServer(new LiftRequest(item, 1)); // unequip
                        Assistant.Client.Instance.SendToServer(new EquipRequest(item.Serial, World.Player, item.Layer)); // Equip
                    }
                }
                else
                {
                    throw new RCERuntimeError(node, $" Invalid layer");
                }
            }
            else
            {
                Spells.Interrupt();
            }

            return true;
        }

        /// <summary>
        /// sound (serial)
        /// </summary>
        private static bool Sound(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length != 1)
            {
                throw new RCERuntimeError(node, "Usage: sound (serial)");
            }

            Assistant.Client.Instance.SendToClient(new PlaySound(vars[0].AsInt(), Player.Position.X ,Player.Position.Y, Player.Position.Z));

            return true;
        }

        /// <summary>
        /// music (id)
        /// </summary>
        private static bool Music(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length != 1)
            {
                throw new RCERuntimeError(node, "Usage: music (id)");
            }

            Assistant.Client.Instance.SendToClient(new PlayMusic(vars[0].AsUShort()));

            return true;
        }

        /// <summary>
        /// cuo (setting) (value)
        /// </summary>
        private static bool ClassicUOProfile(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            return NotImplemented(node, vars, quiet, force);

            if (vars.Length != 2)
            {
                throw new RCERuntimeError(node, "Usage: cuo (setting) (value)");
            }
            /*
            string property = ClassicUOManager.IsValidProperty(vars[0].AsString());

            if (string.IsNullOrEmpty(property))
            {
                throw new RCERuntimeError(node, "Unknown ClassicUO setting/property. Type `>cuo list` for a list of valid settings.");
            }

            bool isNumeric = int.TryParse(vars[0].AsString(), out var value);

            if (isNumeric)
            {
                ClassicUOManager.ProfilePropertySet(property, value);
            }
            else
            {
                switch (vars[1].AsString())
                {
                    case "true":
                        ClassicUOManager.ProfilePropertySet(property, true);
                        break;
                    case "false":
                        ClassicUOManager.ProfilePropertySet(property, false);
                        break;
                    default:
                        ClassicUOManager.ProfilePropertySet(property, vars[1].AsString());
                        break;
                }
            }

            CommandHelper.SendMessage($"ClassicUO Setting: '{property}' set to '{vars[1].AsString()}'", quiet);

            return true;
            */
        }

        /// <summary>
        /// rename (serial) (new_name)
        /// </summary>
        private bool Rename(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: rename (serial) (new_name)");
            }

            string newName = vars[1].AsString();

            if (newName.Length < 1)
            {
                throw new RCERuntimeError(node, "Mobile name must be longer than one character");
            }

            if (World.Mobiles.TryGetValue(vars[0].AsSerial(), out var follower))
            {
                if (follower.CanRename)
                {
                    Misc.PetRename(follower.Serial, newName);
                }
                else
                {
                    SendError("Unable to rename mobile");
                }
            }

            return true;
        }

        private enum GetLabelState
        {
            None,
            WaitingForFirstLabel,
            WaitingForRemainingLabels
        };

        private static GetLabelState _getLabelState = GetLabelState.None;
        private static Action<Packet, PacketHandlerEventArgs, Serial, ushort, MessageType, ushort, ushort, string, string, string> _onLabelMessage;
        private static Action _onStop;

        /// <summary>
        /// getlabel (serial) (name)
        /// </summary>
        private static bool GetLabel(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);

            /*
            if (args.Length != 2)
                throw new RCERuntimeError(node, "Usage: getlabel (serial) (name)");

            var serial = args[0].AsSerial();
            var name = args[1].AsString();

            var mobile = World.FindMobile(serial);
            if (mobile != null)
            {
                if (mobile.IsHuman)
                {
                    return false;
                }
            }

            switch (_getLabelState)
            {
                case GetLabelState.None:
                    _getLabelState = GetLabelState.WaitingForFirstLabel;
                    Interpreter.Timeout(2000, () =>
                    {
                        MessageManager.OnLabelMessage -= _onLabelMessage;
                        _onLabelMessage = null;
                        Interpreter.OnStop -= _onStop;
                        _getLabelState = GetLabelState.None;
                        MessageManager.GetLabelCommand = false;
                        return true;
                    });

                    // Single click the object
                    Assistant.Client.Instance.SendToServer(new SingleClick((Serial)args[0].AsSerial()));

                    // Capture all message responses
                    StringBuilder label = new StringBuilder();

                    // Some messages from Outlands server are send in sequence of LabelType and RegularType
                    // so we want to invoke that _onLabelMessage in both cases with delays
                    MessageManager.GetLabelCommand = true;

                    // Reset the state when script is stopped
                    _onStop = () =>
                    {
                        if (_onLabelMessage != null)
                        {
                            MessageManager.OnLabelMessage -= _onLabelMessage;
                            _onLabelMessage = null;
                        }
                        _getLabelState = GetLabelState.None;

                        Interpreter.OnStop -= _onStop;
                        MessageManager.GetLabelCommand = false;
                    };

                    _onLabelMessage = (p, a, source, graphic, type, hue, font, lang, sourceName, text) =>
                    {
                        if (source != serial)
                            return;

                        a.Block = true;

                        if (_getLabelState == GetLabelState.WaitingForFirstLabel)
                        {
                            // After the first message, switch to a pause instead of a timeout.
                            _getLabelState = GetLabelState.WaitingForRemainingLabels;
                            Interpreter.Pause(500);
                        }

                        label.Append(" " + text);

                        Interpreter.SetVariable(name, label.ToString().Trim());
                    };

                    Interpreter.OnStop += _onStop;
                    MessageManager.OnLabelMessage += _onLabelMessage;

                    break;
                case GetLabelState.WaitingForFirstLabel:
                    break;
                case GetLabelState.WaitingForRemainingLabels:
                    // We get here after the pause has expired.
                    Interpreter.OnStop -= _onStop;
                    MessageManager.OnLabelMessage -= _onLabelMessage;

                    _onLabelMessage = null;
                    _getLabelState = GetLabelState.None;

                    MessageManager.GetLabelCommand = false;

                    return true;
            }

            */
            return false;
        }

        /// <summary>
        /// ignore (serial)
        /// </summary>
        private static bool AddIgnore(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length != 1)
                throw new RCERuntimeError(node, "Usage: ignore (serial)");

            uint serial = vars[0].AsSerial();
            Misc.IgnoreObject((int)serial);

            /*
            Variable toIgnore = vars[0];
            string ignoreListName = vars[0].AsString();

            if (Interpreter.ListExists(ignoreListName))
            {
                List<Serial> list = Interpreter.GetList(ignoreListName).Select(v => (Serial)v.AsSerial()).ToList();
                Interpreter.AddIgnoreRange(list);
                CommandHelper.SendMessage($"Added {list.Count} entries to ignore list", quiet);
            }
            else
            {
                uint serial = toIgnore.AsSerial();
                Interpreter.AddIgnore(serial);
                CommandHelper.SendMessage($"Added {serial} to ignore list", quiet);
            }
            */

            return true;
        }

        /// <summary>
        /// unignore (serial or list)
        /// </summary>
        private static bool RemoveIgnore(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            if (vars.Length != 1)
                throw new RCERuntimeError(node, "Usage: unignore (serial or list)");


            uint serial = vars[0].AsSerial();
            Misc.UnIgnoreObject((int)serial);
            
            /*
            Variable toIgnore = vars[0];
            string ignoreListName = toIgnore.AsString();

            if (Interpreter.ListExists(ignoreListName))
            {
                List<Serial> list = Interpreter.GetList(ignoreListName).Select(v => (Serial)v.AsSerial()).ToList();
                Interpreter.RemoveIgnoreRange(list);
                CommandHelper.SendMessage($"Removed {list.Count} entries from ignore list", quiet);
            }
            else
            {
                uint serial = toIgnore.AsSerial();
                Interpreter.RemoveIgnore(serial);
                CommandHelper.SendMessage($"Removed {serial} from ignore list", quiet);
            }
            */

            return true;
        }

        /// <summary>
        /// clearignore
        /// </summary>
        private static bool ClearIgnore(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            Misc.ClearIgnore();
            if (!quiet)
                Misc.SendMessage("Ignore List cleared");

            return true;
        }

        /// <summary>
        /// cooldown ('name') ('seconds') ['hue'] ['icon'] ['sound'] ['stay visible'] ['foreground color'] ['background color']
        /// </summary>
        private static bool Cooldown(ASTNode node, Argument[] vars, bool quiet, bool force)
        {
            return NotImplemented(node, vars, quiet, force);

            /*
            if (vars.Length < 2)
            {
                throw new RCERuntimeError(node, "Usage: cooldown ('name') ('seconds') ['hue'] ['icon'] ['sound'] ['stay visible'] ['foreground color'] ['background color']");
            }

            string name = vars[0].AsString();
            int seconds = vars[1].AsInt();

            int hue = 0, sound = 0;
            string icon = "none";
            bool stay = false;

            Color foreColor = Color.Empty;
            Color backColor = Color.Empty;

            switch (vars.Length)
            {
                case 3:
                    hue = vars[2].AsInt();

                    break;
                case 4:
                    hue = vars[2].AsInt();
                    icon = vars[3].AsString();

                    break;
                case 5:
                    hue = vars[2].AsInt();
                    icon = vars[3].AsString();
                    sound = vars[4].AsInt();

                    break;
                case 6:
                    hue = vars[2].AsInt();
                    icon = vars[3].AsString();
                    sound = vars[4].AsInt();
                    stay = vars[5].AsBool();

                    break;
                case 7:
                    hue = vars[2].AsInt();
                    icon = vars[3].AsString();
                    sound = vars[4].AsInt();
                    stay = vars[5].AsBool();

                    foreColor = Color.FromName(vars[6].AsString());

                    break;
                case 8:
                    hue = vars[2].AsInt();
                    icon = vars[3].AsString();
                    sound = vars[4].AsInt();
                    stay = vars[5].AsBool();

                    foreColor = Color.FromName(vars[6].AsString());
                    backColor = Color.FromName(vars[7].AsString());

                    break;
            }

            CooldownManager.AddCooldown(new Cooldown
            {
                Name = name,
                EndTime = DateTime.UtcNow.AddSeconds(seconds),
                Hue = hue,
                Icon = icon.Equals("0") ? 0 : BuffDebuffManager.GetGraphicId(icon),
                Seconds = seconds,
                SoundId = sound,
                StayVisible = stay,
                ForegroundColor = foreColor,
                BackgroundColor = backColor
            });

            */

            return true;
        }

        /// <summary>
        /// poplist ('list name') ('element value'/'front'/'back')
        /// </summary>
        private bool PopList(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
                throw new RCERuntimeError( node, "Usage: poplist ('list name') ('element value'/'front'/'back')");

            string frontBack = args[1].AsString().ToLower();
            return m_Interpreter.PopList(args[0].AsString(), (frontBack == "front"));
        }

        /// <summary>
        /// pushlist ('list name') ('element value') ['front'/'back']
        /// </summary>
        private bool PushList(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2 || args.Length > 3)
                throw new RCERuntimeError(node, "Usage: pushlist ('list name') ('element value') ['front'/'back']");

            string listName = args[0].AsString();
            string frontBack = "back";
            if (args.Length == 3)
            {
                frontBack = args[2].AsString().ToLower();
            }

            uint resolvedAlias = m_Interpreter.GetAlias(args[1].AsString());
            Argument insertItem = args[1];
            if (resolvedAlias == uint.MaxValue)
            {
                Utility.Logger.Debug("Pushing {0} to list {1}", insertItem.AsString(), listName);
                m_Interpreter.PushList(listName, insertItem, (frontBack == "front"), false);
            }
            else
            {
                ASTNode node_int = new ASTNode(ASTNodeType.INTEGER, resolvedAlias.ToString(), insertItem.Node, insertItem.Node.LineNumber);
                Argument newArg = new Argument(insertItem._script, node_int);
                Utility.Logger.Debug("Pushing {0} to list {1}", newArg.AsString(), listName);
                m_Interpreter.PushList(listName, newArg, (frontBack == "front"), false);
            }
            return true;
        }

        /// <summary>
        /// removelist ('list name')
        /// </summary>
        private bool RemoveList(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: removelist ('list name')");

            Interpreter.DestroyList(args[0].AsString());

            return true;
        }

        /// <summary>
        /// createlist ('list name')
        /// </summary>
        private bool CreateList(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: createlist ('list name')");

            Interpreter.CreateList(args[0].AsString());

            return true;
        }

        /// <summary>
        /// clearlist ('list name')
        /// </summary>
        private bool ClearList(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: clearlist ('list name')");

            Interpreter.ClearList(args[0].AsString());

            return true;
        }

        /// <summary>
        /// settimer (timer name) (value)
        /// </summary>
        private bool SetTimer(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
                throw new RCERuntimeError(node, "Usage: settimer (timer name) (value)");


            Interpreter.SetTimer(args[0].AsString(), args[1].AsInt());
            return true;
        }

        /// <summary>
        /// removetimer (timer name)
        /// </summary>
        private bool RemoveTimer(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: removetimer (timer name)");

            Interpreter.RemoveTimer(args[0].AsString());
            return true;
        }

        /// <summary>
        /// createtimer (timer name)
        /// </summary>
        private bool CreateTimer(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
                throw new RCERuntimeError(node, "Usage: createtimer (timer name)");

            Interpreter.CreateTimer(args[0].AsString());
            return true;
        }

        private bool PromptAlias(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                RazorEnhanced.Target target = new RazorEnhanced.Target();
                int value = target.PromptTarget("Target Alias for " + alias);
                m_Interpreter.SetAlias(alias, (uint)value);
            }
            return true;
        }







        /// <summary>
        /// land
        /// </summary>
        private static bool LandCommand(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Player.Fly(false);
            return true;
        }

        /// RazorCeEngine.FlyCommand
        /// <summary>
        /// fly
        /// </summary>
        private static bool FlyCommand(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Player.Fly(true);
            return true;
        }

        private static bool Info(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Misc.Inspect();
            return true;
        }





        static internal Dictionary<string, string> map = new Dictionary<string, string>()
            {
                {"north", "North" },
                {"south", "South" },
                {"east", "East" },
                {"west", "West" },
                {"southwest", "Left" },
                {"left", "Left" },
                {"northwest", "Up" },
                {"up", "Up" },
                {"southeast", "Down" },
                {"down", "Down" },
                {"northeast", "Right" },
                {"right", "Right" },
            };

        /// <summary>
        /// pathfindto x y
        /// </summary>
        private bool PathFindTo(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
            {
                SendError("pathfindto requires an X and Y co-ordinates");
                return false;
            }

            int X = args[0].AsInt();
            int Y = args[1].AsInt();
            PathFinding.PathFindTo(X, Y);

            return true;
        }


        /// <summary>
        /// run (direction)
        /// </summary>
        private static bool Run(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0)
                Player.Run(Player.Direction);

            if (args.Length == 1)
            {
                string direction = args[0].AsString().ToLower();
                if (!map.ContainsKey(direction))
                {
                    throw new RCEArgumentError(node, args[0].AsString() + " not recognized.");
                }
                direction = map[direction];
                if (Player.Direction != direction)
                    Player.Walk(direction);
                Player.Run(direction);
            }

            return true;
        }

        /// <summary>
        /// turn (direction)
        /// </summary>
        private static bool Turn(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string direction = args[0].AsString().ToLower();
                if (!map.ContainsKey(direction))
                {
                    throw new RCEArgumentError(node, args[0].AsString() + " not recognized.");
                }
                direction = map[direction];
                if (Player.Direction != direction)
                    Player.Walk(direction);
            }

            return true;
        }


        /// <summary>
        /// clearhands ('left'/'right'/'both')
        /// </summary>
        private static bool ClearHands(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0 || args[0].AsString().ToLower() == "both")
            {
                Player.UnEquipItemByLayer("RightHand", false);
                Misc.Pause(600);
                Player.UnEquipItemByLayer("LeftHand", false);
            }
            if (args.Length == 1)
            {
                if (args[0].AsString().ToLower() == "right")
                    Player.UnEquipItemByLayer("RightHand", false);
                if (args[0].AsString().ToLower() == "left")
                    Player.UnEquipItemByLayer("LeftHand", false);
            }


            return true;
        }

        /// <summary>
        /// clickobject (serial)
        /// </summary>
        private static bool ClickObject(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                int serial = (int)args[0].AsSerial();
                Items.SingleClick(serial);
            }

            return true;
        }

        /// <summary>
        /// bandageself
        /// </summary>
        private static bool BandageSelf(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            BandageHeal.Heal(Assistant.World.Player, false);
            return true;
        }

        /// <summary>
        /// useonce (graphic) [color]
        /// </summary>
        private bool UseOnce(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // This is a bit problematic
            // RCEteam highlights the selected item red in your backpack, and it searches recursively to find the item id
            // Current logic for us, only searches 1 deep .. maybe thats enough for now
            if (args.Length == 0)
            {
                SendError("Insufficient parameters");
                return true;
            }

            int itemID = args[0].AsInt();
            int color = -1;
            if (args.Length > 1)
            {
                color = args[1].AsInt();
            }
            List<Item> items = Player.Backpack.Contains;
            Item selectedItem = null;

            foreach (Item item in items)
            {
                if (item.ItemID == itemID && (color == -1 || item.Hue == color) && (!m_serialUseOnceIgnoreList.Contains(item.Serial)))
                    selectedItem = item;
            }

            if (selectedItem != null)
            {
                m_serialUseOnceIgnoreList.Add(selectedItem.Serial);
                Items.UseItem(selectedItem.Serial);
                Misc.Pause(500);
            }

            return true;
        }

        /// <summary>
        /// clearusequeue resets the use once list
        /// </summary>
        /// 
        private bool CleanUseQueue(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            m_serialUseOnceIgnoreList.Clear();
            return true;
        }


        /// <summary>
        /// (serial) (destination) [(x, y, z)] [amount]
        /// </summary>
        private bool MoveItem(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                SendError("Insufficient parameters");
                return true;
            }
            int source = (int)args[0].AsSerial();
            int dest = (int)args[1].AsSerial();
            int x = -1;
            int y = -1;
            int z = 0;
            int amount = -1;

            if (args.Length == 3)
            {
                amount = args[2].AsInt();
            }

            if (args.Length > 4)
            {
                x = args[2].AsInt();
                y = args[3].AsInt();
                z = args[4].AsInt();
            }
            if (args.Length > 5)
            {
                amount = args[5].AsInt();
            }
            if (dest == -1)
                Items.MoveOnGround(source, amount, x, y, z);
            else
                Items.Move(source, dest, amount, x, y);


            return true;
        }



        /// <summary>
        /// feed (serial) (graphic) [color] [amount]
        /// </summary>
        /// Feed doesn't support food groups etc unless someone adds it
        /// Config has the data now
        private bool Feed(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                SendError("Insufficient parameters");
                return false;
            }
            int target = (int)args[0].AsSerial();
            int graphic = args[1].AsInt();
            int color = -1;
            int amount = 1;

            if (args.Length > 2)
            {
                color = args[2].AsInt();
            }
            if (args.Length > 3)
            {
                amount = args[1].AsInt();
            }
            Item food = Items.FindByID(graphic, color, Player.Backpack.Serial, true);
            if (food != null)
            {
                if (target == Player.Serial)
                    Items.UseItem(food.Serial);
                else
                    Items.Move(food, target, amount);
            }
            return true;
        }

        /// <summary>
        /// togglehands ('left'/'right')
        /// </summary>
        private bool ToggleHands(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string hand = args[0].AsString().ToLower();
                if (hand == "left")
                {
                    Item left = Player.GetItemOnLayer("LeftHand");
                    if (left == null)
                    {
                        if (m_toggle_LeftSave != 0)
                        {
                            Player.EquipItem(m_toggle_LeftSave);
                        }
                    }
                    else
                    {
                        m_toggle_LeftSave = left.Serial;
                        Player.UnEquipItemByLayer("LeftHand", false);
                    }
                }
                if (hand == "right")
                {
                    Item right = Player.GetItemOnLayer("RightHand");
                    if (right == null)
                    {
                        if (m_toggle_RightSave != 0)
                        {
                            Player.EquipItem(m_toggle_RightSave);
                        }
                    }
                    else
                    {
                        m_toggle_RightSave = right.Serial;
                        Player.UnEquipItemByLayer("RightHand", false);
                    }
                }
            }
            return true;
        }


        //TODO: Not implemented properly .. I dunno how to do a party only msg
        /// <summary>
        /// partymsg ('text') [color] [serial]
        /// </summary>
        private static bool PartyMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatParty(msg);
            }

            if (args.Length >= 2)
            {
                string msg = args[0].AsString();
                // 2nd parameter of ChatParty is Serial, to send private messages, not color, what0's
                int serial = args[1].AsInt();
                Player.ChatParty(msg, serial);
            }

            return true;
        }

        /// <summary>
        /// msg text [color]
        /// </summary>
        private static bool MsgCommand(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string msg = args[0].AsString();
            if (args.Length == 1)
            {
                Player.ChatSay(0, msg);
            }
            if (args.Length == 2)
            {
                int color = args[1].AsInt();
                Player.ChatSay(color, msg);
            }

            return true;
        }

        /// <summary>
        /// moveitemoffset (serial) 'ground' [(x, y, z)] [amount] 
        /// </summary>
        private static bool MoveItemOffset(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            uint serial = args[0].AsSerial();
            // string ground = args[1].AsString();
            if (args.Length == 2)
            {
                Items.DropItemGroundSelf((int)serial);
            }
            else
            {
                int amount;
                if (args.Length == 3)
                {
                    amount = args[2].AsInt();
                    Items.DropItemGroundSelf((int)serial, amount);
                }
                else if (args.Length >= 5)
                {
                    var ppos = Player.Position;
                    int X = args[2].AsInt() + ppos.X;
                    int Y = args[3].AsInt() + ppos.Y;
                    int Z = args[4].AsInt() + ppos.Z;

                    amount = (args.Length == 6) ? args[5].AsInt() : -1;
                    Items.MoveOnGround((int)serial, amount, X, Y, Z);
                }
                else
                {
                    WrongParameterCount(node, 5, args.Length, "Valid args num: 2,3,5,6");
                }
            }

            return true;
        }

        /// <summary>
        /// movetype (graphic) (source) (destination) [(x, y, z)] [color] [amount] [range or search level]
        /// </summary>
        private static IComparable MoveType(ASTNode node, Argument[] args, bool quiet)
        {
            switch (args.Length)
            {
                case 3:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        uint dest = args[2].AsSerial();
                        Item item = Items.FindByID(id, -1, (int)src, true);
                        if (item != null)
                        {
                            Items.Move(item.Serial, (int)dest, -1);
                            return true;
                        }
                        break;
                    }

                case 4:
                    {
                        WrongParameterCount(node, 4, args.Length, "Co-ordinates requires x y and z");
                        break;
                    }

                case 5:
                case 6:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        uint dest = args[2].AsSerial();
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        // uint z = args[5].AsUInt(); unused

                        Item item = Items.FindByID(id, -1, (int)src, true);
                        if (item != null)
                        {
                            if (x == 0 && y == 0)
                                Items.Move(item.Serial, (int)dest, -1);
                            else
                                Items.Move(item.Serial, (int)dest, -1, x, y);
                            return true;
                        }
                        break;
                    }

                case 7:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        uint dest = args[2].AsSerial();
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        // uint z = args[5].AsUInt(); unused
                        int color = args[6].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, true);
                        if (item != null)
                        {
                            if (x == 0 && y == 0)
                                Items.Move(item.Serial, (int)dest, -1);
                            else
                                Items.Move(item.Serial, (int)dest, -1, x, y);
                            return true;
                        }
                        break;
                    }

                case 8:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        uint dest = args[2].AsSerial();
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        // uint z = args[5].AsUInt(); unused
                        int color = args[6].AsInt();
                        int amount = args[7].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, true);
                        if (item != null)
                        {
                            if (x == 0 && y == 0)
                                Items.Move(item.Serial, (int)dest, amount);
                            else
                                Items.Move(item.Serial, (int)dest, amount, x, y);
                            return true;
                        }
                        break;
                    }

                case 9:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        uint dest = args[2].AsSerial();
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        // uint z = args[5].AsUInt(); unused
                        int color = args[6].AsInt();
                        int amount = args[7].AsInt();
                        int range = args[8].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, range);
                        if (item != null)
                        {
                            if (x == 0 && y == 0)
                                Items.Move((int)item.Serial, (int)dest, amount);
                            else
                                Items.Move(item.Serial, (int)dest, amount, x, y);
                            return true;
                        }
                        break;
                    }

                default:
                    WrongParameterCount(node, 4, args.Length, "Invalid parameters specified");
                    break;
            }


            return false;
        }
        /// <summary>
        /// movetypeoffset (graphic) (source) 'ground' [(x, y, z)] [color] [amount] [range or search level]
        /// </summary>
        private static bool MoveTypeOffset(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            switch (args.Length)
            {
                case 2:
                case 3:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        Item item = Items.FindByID(id, -1, (int)src, true);
                        if (item != null)
                        {
                            Items.DropItemGroundSelf(item.Serial); // this seldom works because of server limitations
                        }
                        return true;
                    }
                    break;
                case 4:
                    {
                        WrongParameterCount(node, 4, args.Length, "Co-ordinates requires x y and z");
                        return false;
                    }
                    break;
                case 5:
                case 6:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        //uint dest = args[2].AsSerial(); //ignored, in docs it says 'ground'
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        int z = args[5].AsInt();

                        Item item = Items.FindByID(id, -1, (int)src, true);
                        if (item != null)
                        {
                            Items.MoveOnGround(item.Serial, -1, (int)Player.Position.X + x, (int)Player.Position.Y + y, (int)Player.Position.Z + z);
                            return true;
                        }
                    }
                    break;
                case 7:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        //uint dest = args[2].AsSerial(); //ignored, in docs it says 'ground'
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        int z = args[5].AsInt();
                        int color = args[6].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, true);
                        if (item != null)
                        {
                            Items.MoveOnGround(item.Serial, -1, Player.Position.X + x, Player.Position.Y + y, Player.Position.Z + z);
                            return true;
                        }
                    }
                    break;
                case 8:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        //uint dest = args[2].AsSerial(); //ignored, in docs it says 'ground'
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        int z = args[5].AsInt();
                        int color = args[6].AsInt();
                        int amount = args[7].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, true);
                        if (item != null)
                        {
                            Items.MoveOnGround(item.Serial, amount, Player.Position.X + x, Player.Position.Y + y, Player.Position.Z + z);
                            return true;
                        }
                    }
                    break;
                case 9:
                    {
                        int id = args[0].AsInt();
                        uint src = args[1].AsSerial();
                        //uint dest = args[2].AsSerial(); //ignored, in docs it says 'ground'
                        int x = args[3].AsInt();
                        int y = args[4].AsInt();
                        int z = args[5].AsInt();
                        int color = args[6].AsInt();
                        int amount = args[7].AsInt();
                        int range = args[8].AsInt();

                        Item item = Items.FindByID(id, color, (int)src, range, true);
                        if (item != null)
                        {
                            Items.MoveOnGround(item.Serial, amount, Player.Position.X + x, Player.Position.Y + y, Player.Position.Z + z);
                            return true;
                        }
                    }
                    break;
                default:
                    return false;
            }
            return true;
        }

        /// <summary>
        /// equipitem (serial) (layer)
        /// </summary>
        private static bool EquipItem(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                //int layer = args[1].AsInt();
                if (World.FindItem(serial) != null)
                {
                    Player.EquipItem((int)serial);
                    return true;
                }
            }
            return false;
        }
        /// <summary>
        /// togglemounted
        /// </summary>
        private bool ToggleMounted(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // rceteam has a crappy implementation
            // I am gonna change how it works a bit
            if (null != Player.Mount)
            {
                m_lastMount = Player.Mount.Serial;
                m_Interpreter.SetAlias("mount", (uint)m_lastMount);
                Mobiles.UseMobile(Player.Serial);
            }
            else
            {
                if (m_lastMount == 0)
                {
                    m_lastMount = (int)m_Interpreter.GetAlias("mount");
                    if (m_lastMount == 0)
                    {
                        RazorEnhanced.Target target = new RazorEnhanced.Target();
                        m_lastMount = target.PromptTarget("Select a new mount");
                    }
                }
                Mobile mount = Mobiles.FindBySerial(m_lastMount);
                if (mount != null)
                {
                    Items.UseItem(mount.Serial);
                }

            }
            return true;
        }

        /// <summary>
        /// NOT IMPLEMENTED
        /// </summary>
        private static bool EquipWand(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }
        /// <summary>
        /// buy ('list name')
        /// </summary>
        private bool Buy(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string buyListName = args[0].AsString().ToLower();
                if (Settings.BuyAgent.ListExists(buyListName))
                {
                    BuyAgent.ChangeList(buyListName);
                    BuyAgent.Enable();
                    return true;
                }
                else
                {
                    SendError(String.Format("Buy List {0} does not exist", buyListName));
                }

            }
            return false;
        }

        /// <summary>
        /// sell ('list name')
        /// </summary>
        private bool Sell(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string sellListName = args[0].AsString().ToLower();
                if (Settings.SellAgent.ListExists(sellListName))
                {
                    SellAgent.ChangeList(sellListName);
                    SellAgent.Enable();
                    return true;
                }
                else
                {
                    SendError(String.Format("Sell List {0} does not exist", sellListName));
                }

            }
            return false;

        }
        /// <summary>
        /// clearbuy
        /// </summary>
        private static bool ClearBuy(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            BuyAgent.Disable();
            return true;
        }

        /// <summary>
        /// clearsell
        /// </summary>
        private static bool ClearSell(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            SellAgent.Disable();
            return true;
        }

        /// <summary>
        /// restock ('profile name') [source] [destination] [dragDelay]
        /// </summary>
        private static bool Restock(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            int src = -1;
            int dst = -1;
            int delay = -1;
            string restockName = null;

            if (args.Length >= 1)
            {
                restockName = args[0].AsString();
            }
            if (args.Length >= 2)
            {
                src = (int)args[1].AsSerial();
            }
            if (args.Length >= 3)
            {
                dst = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                delay = (int)args[3].AsSerial();
            }

            if (restockName != null)
            {
                RazorEnhanced.Restock.RunOnce(restockName, src, dst, delay);
                int max = 30 * 2; // max 30 seconds @ .5 seconds each loop
                while (RazorEnhanced.Restock.Status() == true && (max-- > 0))
                {
                    System.Threading.Thread.Sleep(100);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// organizer ('profile name') [source] [destination] [dragDelay]
        /// </summary>
        private static bool Organizer(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            int src = -1;
            int dst = -1;
            int delay = -1;
            string organizerName = null;

            if (args.Length >= 1)
            {
                organizerName = args[0].AsString();
            }
            if (args.Length >= 2)
            {
                src = (int)args[1].AsSerial();
            }
            if (args.Length >= 3)
            {
                dst = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                delay = (int)args[3].AsSerial();
            }

            if (organizerName != null)
            {
                RazorEnhanced.Organizer.RunOnce(organizerName, src, dst, delay);
                int max = 30 * 2; // max 30 seconds @ .5 seconds each loop
                while (RazorEnhanced.Organizer.Status() == true && (max-- > 0))
                {
                    System.Threading.Thread.Sleep(100);
                }
                return true;
            }

            return false;
        }

        private static bool AutoTargetObject(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Assistant.Targeting.SetAutoTarget(serial);

                return true;
            }
            return false;
        }

        /// <summary>
        /// autoloot - NOT IMPLEMENTED
        /// </summary>
        private static bool Autoloot(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }

        /// <summary>
        /// dressconfig What is this supposed to do ? NOT IMPLEMENTED
        /// </summary>
        private static bool DressConfig(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }

        /// <summary>
        /// toggleautoloot
        /// </summary>
        private static bool ToggleAutoloot(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (RazorEnhanced.AutoLoot.Status())
            {
                RazorEnhanced.AutoLoot.Stop();
            }
            else
            {
                RazorEnhanced.AutoLoot.Start();
            }
            return true;
        }

        /// <summary>
        /// togglescavenger
        /// </summary>
        private static bool ToggleScavenger(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (RazorEnhanced.Scavenger.Status())
            {
                RazorEnhanced.Scavenger.Stop();
            }
            else
            {
                RazorEnhanced.Scavenger.Start();
            }
            return true;
        }

        /// <summary>
        /// counter ('format') (operator) (value) NOT IMPLEMENTED
        /// </summary>
        private static bool Counter(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }


        /// <summary>
        /// waitforjournal ('text') (timeout) 
        /// </summary>
        private bool WaitForJournal(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                string text = args[0].AsString();
                int delay = args[1].AsInt();
                return m_journal.WaitJournal(text, delay);
            }
            return false;
        }

        /// <summary>
        /// ping
        /// </summary>
        private static bool Ping(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Assistant.Commands.Ping(null);
            return true;
        }

        /// <summary>
        /// resync
        /// </summary>
        private static bool Resync(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Misc.Resync();
            return true;
        }

        /// <summary>
        /// snapshot 
        /// </summary>
        private static bool Snapshot(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Assistant.ScreenCapManager.CaptureNow();
            return true;
        }

        /// <summary>
        /// where
        /// </summary>
        private static bool Where(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Assistant.Commands.Where(null);
            return true;
        }

        /// <summary>
        /// messagebox ('title') ('body')
        /// </summary>
        private static bool MessageBox(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string title = "not specified";
            string body = "empty";
            if (args.Length > 0)
            {
                title = args[0].AsString();
            }
            if (args.Length > 1)
            {
                title = args[1].AsString();
            }
            var result = RazorEnhanced.UI.RE_MessageBox.Show(title,
                body,
                ok: "Yes", no: "No", cancel: null, backColor: null);

            if (result == DialogResult.OK || result == DialogResult.Yes)
                return true;
            return false;
        }

        /// <summary>
        /// mapuo NOT IMPEMENTED
        /// </summary>
        private static bool MapUO(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        public const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const int MOUSEEVENTF_LEFTUP = 0x0004;
        public const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const int MOUSEEVENTF_RIGHTUP = 0x0010;

        //This simulates a left mouse click
        internal static void LeftMouseClick(int xpos, int ypos)
        {
            SetCursorPos(xpos, ypos);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, xpos, ypos, 0, 0);
            mouse_event(MOUSEEVENTF_RIGHTUP, xpos, ypos, 0, 0);
        }
        internal static void RightMouseClick(int xpos, int ypos)
        {
            SetCursorPos(xpos, ypos);
            mouse_event(MOUSEEVENTF_LEFTDOWN, xpos, ypos, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, xpos, ypos, 0, 0);
        }

        /// <summary>
        /// clickscreen (x) (y) ['single'/'double'] ['left'/'right']
        /// </summary>
        private bool ClickScreen(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                SendError("Invalid parameters");
                return true;
            }
            int x = args[0].AsInt();
            int y = args[1].AsInt();
            string singleDouble = "single";
            string leftRight = "left";

            if (args.Length > 2)
                singleDouble = args[2].AsString().ToLower();
            if (args.Length > 3)
                leftRight = args[3].AsString().ToLower();

            if (leftRight == "left")
            {
                LeftMouseClick(x, y);
                if (singleDouble == "double")
                {
                    System.Threading.Thread.Sleep(50);
                    LeftMouseClick(x, y);
                }
            }
            else
            {
                RightMouseClick(x, y);
                if (singleDouble == "double")
                {
                    System.Threading.Thread.Sleep(50);
                    RightMouseClick(x, y);
                }
            }

            return true;
        }

        /// <summary>
        /// paperdoll
        /// </summary>
        private static bool Paperdoll(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            Misc.OpenPaperdoll();

            return true;
        }

        /// <summary>
        /// helpbutton  NOT IMPLEMENTED
        /// </summary>
        private static bool HelpButton(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(node, args, quiet, force);
        }

        /// <summary>
        /// guildbutton 
        /// </summary>
        private static bool GuildButton(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Player.GuildButton();
            return true;
        }

        /// <summary>
        /// questsbutton 
        /// </summary>
        private static bool QuestsButton(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Player.QuestButton();
            return true;
        }

        /// <summary>
        /// logoutbutton 
        /// </summary>
        private static bool LogoutButton(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Misc.Disconnect();
            return true;
        }

        /// <summary>
        /// virtue('honor'/'sacrifice'/'valor') 
        /// </summary>
        private static bool Virtue(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string virtue = args[0].AsString();
                Player.InvokeVirtue(virtue);
                return true;
            }

            return false;
        }

        /// <summary>
        /// guildmsg ('text')
        /// </summary>
        private static bool GuildMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatGuild(msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// allymsg ('text')
        /// </summary>
        private static bool AllyMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatAlliance(msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// whispermsg ('text') [color]
        /// </summary>
        private static bool WhisperMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 40;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatWhisper(color, msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// yellmsg ('text') [color]
        /// </summary>
        private static bool YellMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 170;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatYell(color, msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// location (serial)
        /// </summary>
        private bool Location(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            uint serial = args[0].AsSerial();
            Mobile m = Mobiles.FindBySerial((int)serial);
            if (m != null)
            {
                SendOutput(String.Format("Position({0}, {1})", m.Position.X, m.Position.Y));
                return true;
            }

            return false;
        }

        /// <summary>
        /// chatmsg (text) [color]
        /// </summary>
        private static bool ChatMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 70;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatSay(color, msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// emotemsg (text) [color]
        /// </summary>
        private static bool EmoteMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 70;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatEmote(color, msg);
                return true;
            }
            return false;
        }

        /// <summary>
        /// timermsg (delay) (text) [color]
        /// </summary>
        private static bool TimerMsg(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            //Verrify/Guessing parameter order.
            if (args.Length == 2)
            {
                var delay = args[0].AsInt();
                var msg = args[1].AsString();
                var color = 20;
                if (args.Length == 3)
                {
                    color = args[2].AsInt();
                }
                Task.Delay(delay).ContinueWith(t => Misc.SendMessage(msg, color));
                return true;
            }
            return false;
        }

        /// <summary>
        /// addfriend [serial]
        /// </summary>
        private static bool AddFriend(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // docs say something about options, guessing thats the selection ?
            // docs sucks and I stuggle to find examples on what params it takes
            //
            // TODO: Hypothetical implementation: 0 args -> prompt for serial, 1 arg = serial
            // once verified, remove NotImplemented below
            var list_name = DEFAULT_FRIEND_LIST;
            if (!RazorEnhanced.Settings.Friend.ListExists(list_name))
            {
                RazorEnhanced.Settings.Friend.ListInsert(list_name, true, true, false, false, false, false, false);
            }

            int serial = -1;
            if (args.Length == 0)
            {
                serial = new Target().PromptTarget();
            }
            else if (args.Length == 1)
            {
                serial = args[0].AsInt();
            }


            if (serial > 0)
            {
                var new_friend = Mobiles.FindBySerial(serial);
                string name = new_friend.Name;
                Friend.AddPlayer(list_name, name, serial);
                return true;
            }
            return false;
        }

        /// <summary>
        /// removefriend NOT IMPLEMENTED
        /// </summary>
        private static bool RemoveFriend(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            var list_name = DEFAULT_FRIEND_LIST;
            if (RazorEnhanced.Settings.Friend.ListExists(list_name))
            {
                int serial = -1;
                if (args.Length == 0)
                {
                    serial = new Target().PromptTarget();
                }
                else if (args.Length == 1)
                {
                    serial = args[0].AsInt();
                }
                return Friend.RemoveFriend(list_name, serial);
            }
            return false;
        }

        /// <summary>
        /// setskill ('skill name') ('locked'/'up'/'down')
        /// </summary>
        private static bool SetSkill(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                string skill = args[0].AsString().ToLower();
                string action = args[1].AsString().ToLower();
                int setAs = 0;
                switch (action)
                {
                    case "up":
                        setAs = 0;
                        break;
                    case "down":
                        setAs = 1;
                        break;
                    case "locked":
                        setAs = 2;
                        break;
                }
                Player.SetSkillStatus(skill, setAs);
                return true;
            }
            return false;
        }

        /// <summary>
        /// waitforproperties (serial) (timeout)
        /// </summary>
        private static bool WaitForProperties(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                int timeout = args[1].AsInt();
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    Items.WaitForProps(item, timeout);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// autocolorpick (color) (dyesSerial) (dyeTubSerial)
        /// </summary>
        private bool AutoColorPick(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 3)
            {
                SendError("Usage is: autocolorpick color dyesSerial dyeTubSerial");
                WrongParameterCount(node, 3, args.Length, "Usage is: autocolorpick color dyesSerial dyeTubSerial");

            }
            int color = args[0].AsInt();
            uint dyesSerial = args[1].AsSerial();
            uint dyeTubSerial = args[2].AsSerial();
            Item dyes = Items.FindBySerial((int)dyesSerial);
            Item dyeTub = Items.FindBySerial((int)dyeTubSerial);
            if (dyes == null) { SendError("autocolorpick: error: can't find dyes with serial " + dyesSerial); }
            if (dyeTub == null) { SendError("autocolorpick: error: can't find dye tub with serial " + dyeTubSerial); }
            if (dyes != null && dyeTub != null)
            {
                Items.ChangeDyeingTubColor(dyes, dyeTub, color);
                return true;
            }
            return false;
        }

        /// <summary>
        /// waitforcontents (serial) (timeout)
        /// </summary>
        private static bool WaitForContents(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                int timeout = args[0].AsInt();
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    Items.WaitForContents(item, timeout);
                    return true;
                }
            }
            return false;
        }


        //Not a RCE Function, utility method for autocure within big/small heal
        private static bool SelfCure()
        {
            if (Player.Poisoned)
            {
                RazorEnhanced.Target.Cancel();

                Spells.CastMagery("Cure");
                RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay
                if (RazorEnhanced.Target.HasTarget())
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    return false;
                }
                RazorEnhanced.Target.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// miniheal [serial]
        /// </summary>
        private static bool MiniHeal(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (SelfCure()) { return true; }

            RazorEnhanced.Target.Cancel();
            Spells.CastMagery("Heal");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget())
            {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
                return true;
            }

            return false;
        }

        /// <summary>
        /// bigheal [serial]
        /// </summary>
        private static bool BigHeal(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (SelfCure()) { return true; }

            RazorEnhanced.Target.Cancel();
            Spells.CastMagery("Greater Heal");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget())
            {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// chivalryheal [serial] 
        /// </summary>
        private static bool ChivalryHeal(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            RazorEnhanced.Target.Cancel();
            Spells.CastChivalry("Close Wounds");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget())
            {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// waitfortarget (timeout)
        /// </summary>
        private static bool WaitForTarget(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            int delay = 1000;
            bool show = false;
            if (args.Length > 0)
            {
                delay = args[0].AsInt();
            }
            if (args.Length > 1)
            {
                show = args[1].AsBool();
            }

            return RazorEnhanced.Target.WaitForTarget(delay, show);
        }

        /// <summary>
        /// cancelautotarget
        /// </summary>
        private static bool CancelAutoTarget(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            Assistant.Targeting.CancelAutoTarget();
            return true;
        }

        /// <summary>
        /// canceltarget
        /// </summary>
        private static bool CancelTarget(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            //https://discord.com/channels/292282788311203841/383331237269602325/839987031853105183
            //Target.Execute(0x0) is a better form of Target.Cancel
            RazorEnhanced.Target.TargetExecute(0x0);
            //RazorEnhanced.Target.Cancel();
            return true;
        }

        /// <summary>
        /// targetresource (serial) ('ore'/'sand'/'wood'/'graves'/'red mushrooms')
        /// </summary>
        private static bool TargetResource(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // targetresource serial (ore/sand/wood/graves/red mushrooms)
            if (args.Length != 2)
            {
                WrongParameterCount(node, 2, args.Length);
                return false;
            }
            uint tool = args[0].AsSerial();
            string resource = args[1].AsString();
            RazorEnhanced.Target.TargetResource((int)tool, resource);

            return true;

            /*
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                RazorEnhanced.Target.TargetExecute((int)serial);
            }
            return true;
            */
        }

        /// <summary>
        /// target (serial)
        /// </summary>
        private static bool Target(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                RazorEnhanced.Target.TargetExecute((int)serial);

                return true;
            }
            return false;
        }

        /// <summary>
        /// getenemy ('notoriety') ['filter']
        /// </summary>
        private bool GetEnemy(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0) { WrongParameterCount(node, 1, args.Length); }

            RazorEnhanced.Mobiles.Filter filter = new RazorEnhanced.Mobiles.Filter();
            filter.CheckIgnoreObject = true;

            bool nearest = false;
            foreach (var arg in args)
            {
                string argStr = arg.AsString().ToLower();
                switch (argStr)
                {
                    case "friend":
                        filter.Notorieties.Add(1);
                        break;
                    case "innocent":
                        filter.Notorieties.Add(2);
                        break;
                    case "criminal":
                        filter.Notorieties.Add(4);
                        break;
                    case "gray":
                        filter.Notorieties.Add(3);
                        filter.Notorieties.Add(4);
                        break;
                    case "murderer":
                        filter.Notorieties.Add(6);
                        break;
                    case "enemy":
                        filter.Notorieties.Add(6);
                        filter.Notorieties.Add(5);
                        filter.Notorieties.Add(4);
                        break;
                    case "humanoid":
                        filter.IsHuman = 1;
                        break;
                    case "transformation":
                    //TODO: add ids for transformations: ninja, necro, polymorpjh(?), etc
                    case "closest":
                    case "nearest":
                        nearest = true;
                        break;
                }
            }

            var list = Mobiles.ApplyFilter(filter);
            if (list.Count > 0)
            {
                Mobile anEnemy = list[0];
                if (nearest)
                {
                    anEnemy = Mobiles.Select(list, "Nearest");
                }

                int color = 20;
                switch (anEnemy.Notoriety)
                {
                    case 1: color = 168; break; //Green
                    case 2: color = 190; break; //Blue
                    case 3:
                    case 4: color = 1000; break; //Gray
                    case 5: color = 140; break; //Orange
                    case 6: color = 138; break; //Red
                    case 7: color = 153; break; //Yellow
                }
                RazorEnhanced.Target.SetLast(anEnemy.Serial); //Attempt to highlight
                if (!quiet)
                    Player.HeadMessage(color, "[Enemy] " + anEnemy.Name);
                m_Interpreter.SetAlias("enemy", (uint)anEnemy.Serial);
                return true;
            }

            // No enemies in list
            m_Interpreter.UnSetAlias("enemy");
            return false;
        }

        /// <summary>
        /// getfriend ('notoriety') ['filter']
        /// </summary>
        private bool GetFriend(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0) { WrongParameterCount(node, 1, args.Length); }

            RazorEnhanced.Mobiles.Filter filter = new RazorEnhanced.Mobiles.Filter();
            filter.CheckIgnoreObject = true;
            bool nearest = false;
            foreach (var arg in args)
            {
                string argStr = arg.AsString().ToLower();
                switch (argStr)
                {
                    case "friend":
                        filter.Notorieties.Add(1);
                        break;
                    case "innocent":
                        filter.Notorieties.Add(2);
                        break;
                    case "criminal":
                    case "gray":
                        filter.Notorieties.Add(3);
                        filter.Notorieties.Add(4);
                        break;
                    case "murderer":
                        filter.Notorieties.Add(6);
                        break;
                    case "enemy":
                        filter.Notorieties.Add(6);
                        filter.Notorieties.Add(5);
                        filter.Notorieties.Add(4);
                        break;
                    case "invulnerable":
                        filter.Notorieties.Add(7);
                        break;
                    case "humanoid":
                        filter.IsHuman = 1;
                        break;
                    case "transformation":
                    //TODO: add ids for transformations: ninja, necro, polymorpjh(?), etc
                    case "closest":
                    case "nearest":
                        nearest = true;
                        break;
                }
            }
            var list = Mobiles.ApplyFilter(filter);
            if (list.Count > 0)
            {
                Mobile anEnemy = list[0];
                if (nearest)
                {
                    anEnemy = Mobiles.Select(list, "Nearest");
                }

                int color = 20;
                switch (anEnemy.Notoriety)
                {
                    case 1: color = 190; break; //Blue
                    case 2: color = 168; break; //Green
                    case 3:
                    case 4: color = 1000; break; //Gray
                    case 5: color = 140; break; //Orange
                    case 6: color = 138; break; //Red
                    case 7: color = 153; break; //Yellow
                }
                RazorEnhanced.Target.SetLast(anEnemy.Serial); //Attempt to highlight
                if (!quiet)
                    Player.HeadMessage(color, "[Friend] " + anEnemy.Name);
                m_Interpreter.SetAlias("friend", (uint)anEnemy.Serial);
                return true;
            }

            m_Interpreter.UnSetAlias("friend");
            return false;
        }
        /// <summary>
        /// script ('run'|'stop'|'suspend'|'resume'|'isrunning'|'issuspended') [script_name] [output_alias]
        /// </summary>
        private bool ManageScripts(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            var command = node.Lexeme;
            if (args.Length < 1) { WrongParameterCount(node, 1, args.Length); }
            var operation = args[0].AsString();
            var script_name = args.Length >= 2 ? args[1].AsString() : Misc.ScriptCurrent(true);
            var output_alias = args.Length == 3 ? args[2].AsString() : script_name + (operation == "isrunning" ? "_running" : "_suspended");
            var cmd = $"{command} {operation}";
            switch (operation)
            {
                case "run": Misc.ScriptRun(script_name); break;
                case "stop": Misc.ScriptStop(script_name); break;
                case "suspend": Misc.ScriptSuspend(script_name); break;
                case "resume": Misc.ScriptResume(script_name); break;
                case "isrunning":
                    var running = Misc.ScriptStatus(script_name);
                    m_Interpreter.SetAlias(output_alias, (uint)(running ? 1 : 0));
                    break;
                case "issuspended":
                    var suspended = Misc.ScriptIsSuspended(script_name);
                    m_Interpreter.SetAlias(output_alias, (uint)(suspended ? 1 : 0));
                    break;
                default:
                    throw new RCEArgumentError(node, cmd + " not recognized.");
            }
            return true;
        }

        /// <summary>
        /// namespace 'isolation' (true|false)
        /// namespace 'list'
        /// namespace ('create'|'activate'|'delete') (namespace_name) 
        /// namespace 'move' (new_namespace_name) [old_namespace_name] ['merge'|'replace']
        /// namespace ('get'|'set'|'print') (namespace_name) ['all'|'alias'|'lists'|'timers'] [source_name] [destination_name]
        /// namespace 'print' [namespace_name] ['all'|'alias'|'lists'|'timers'] [name]
        /// </summary>
        private bool ManageNamespaces(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string command = node.Lexeme;
            var cmd = command;
            if (args.Length < 1) { WrongParameterCount(node, 1, args.Length); }
            var operation = args[0].AsString();
            cmd = $"{cmd}_{operation}";
            if (operation == "list")
            {
                return ManageNamespaces_List(node, cmd, args, quiet, force);
            }
            else if (operation == "isolation")
            {
                return ManageNamespaces_Isolation(node, cmd, args, quiet, force);
            }
            else if (operation == "activate" || operation == "create")
            {
                return ManageNamespaces_CreateActivate(node, cmd, args, quiet, force);
            }
            else if (operation == "delete")
            {
                return ManageNamespaces_Delete(node, cmd, args, quiet, force);
            }
            else if (operation == "move")
            {
                return ManageNamespaces_Move(node, cmd, args, quiet, force);
            }
            else if (operation == "print")
            {
                return ManageNamespaces_Print(node, cmd, args, quiet, force);
            }
            else if (operation == "get" || operation == "set")
            {
                return ManageNamespaces_SetGet(node, cmd, args, quiet, force);
            }
            else
            {
                throw new RCEArgumentError(node, cmd + " not recognized.");
            }

            return false;
        }

        private bool ManageNamespaces_List(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1) { WrongParameterCount(node, 2, args.Length); }
            //var toListName = (args.Length == 2) ? args[1].ToString() : null;
            //if (toListName != null)
            //{
            //m_Interpreter.CreateList(toListName);
            //Namespace.List().Apply();
            //Namespace._lists[toListName] = new List<Argument>();
            //
            //}
            //else
            //{    }
            SendOutput("Namespaces:");
            foreach (var name in Namespace.List())
            {
                SendOutput(name);
            }
            //
            return true;
        }

        private bool ManageNamespaces_Isolation(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2) { WrongParameterCount(node, 2, args.Length); }
            this.UseIsolation = args[1].AsBool();
            return true;
        }
        private bool ManageNamespaces_CreateActivate(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            var namespace_name = args.Length >= 2 ? args[1].AsString() : null;
            var operation = args[0].AsString();
            if (args.Length < 2) { WrongParameterCount(node, 2, args.Length); }
            var ns = Namespace.Get(namespace_name);
            if (operation == "activate") { this.Namespace = ns; }
            return true;

        }
        private bool ManageNamespaces_Delete(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            var cur_namespace_name = Namespace.Name;
            var namespace_name = args.Length >= 2 ? args[1].AsString() : null;
            if (cur_namespace_name == namespace_name)
            {
                this.Namespace = Namespace.Get();
            }
            Namespace.Delete(namespace_name);
            return true;
        }
        private bool ManageNamespaces_Move(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {

            if (args.Length < 2) { WrongParameterCount(node, 2, args.Length); }
            var namespace_name = args[1].AsString();

            var old_name = Namespace.Name;
            var new_name = namespace_name;

            if (args.Length >= 3)
            {
                old_name = args[2].AsString();
            }

            var merge = args.Length == 4 && args[3].AsString() == "merge";
            var replace = args.Length == 4 && args[3].AsString() == "replace";
            merge = (!merge && !replace);
            replace = !merge;

            var cmd = $"{command} '{new_name}'" + (replace ? " 'replace'" : "") + (merge ? " 'merge'" : "");
            var didMove = Namespace.Move(old_name, new_name, replace);
            if (!didMove)
            {
                throw new RCEArgumentError(node, cmd + $" failed, old name '{old_name}' not found.");
            }
            return true;
        }
        /// namespace 'print' [namespace_name] ['all'|'alias'|'lists'|'timers'] [name]
        private bool ManageNamespaces_Print(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            var operation = args[0].AsString();
            var namespace_name = args.Length >= 2 ? args[1].AsString() : Namespace.Name;
            var item_type = args.Length >= 3 ? args[2].AsString() : "all";
            var item_name = args.Length == 4 ? args[3].AsString() : null;

            if (!Namespace.Has(namespace_name))
            {
                throw new RCEArgumentError(node, $" {command} namespace '{namespace_name}' not found.");
            }

            var cmd = $"{command} '{namespace_name}' '{item_type}'";
            var ns = Namespace.Get(namespace_name);
            var content = "";
            if (item_type == "all")
            {
                content = Namespace.PrintAll(namespace_name, item_name);
            }
            else if (item_type == "alias")
            {
                content = Namespace.PrintAlias(namespace_name, item_name);
            }
            else if (item_type == "lists")
            {
                content = Namespace.PrintLists(namespace_name, item_name);
            }
            else if (item_type == "timers")
            {
                content = Namespace.PrintTimers(namespace_name, item_name);
            }
            else
            {
                throw new RCEArgumentError(node, $"{cmd} items kind must be either: 'all', 'alias', 'lists', 'timers'  ");
            }
            SendOutput(content);
            return true;
        }
        private bool ManageNamespaces_SetGet(ASTNode node, string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2) { WrongParameterCount(node, 2, args.Length); }
            var operation = args[0].AsString();
            var namespace_name = args[1].AsString();

            if (!Namespace.Has(namespace_name))
            {
                throw new RCEArgumentError(node, $"{command} namespace '{namespace_name}' not found.");
            }

            var item_type = args.Length >= 3 ? args[2].AsString() : "all";
            var cmd = $"{command} '{namespace_name}' '{item_type}'";
            var ns = Namespace.Get(namespace_name);

            var namespace_src = (operation == "get" ? ns.Name : this.Namespace.Name);
            var namespace_dst = (operation == "get" ? this.Namespace.Name : ns.Name);
            var name_src = args.Length >= 4 ? args[3].AsString() : null;
            var name_dst = args.Length >= 5 ? args[4].AsString() : null;

            // 'alias'|'lists'|'timers'|'all'
            var copy_ok = false;
            if (item_type == "all")
            {
                copy_ok = Namespace.CopyAll(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "alias")
            {
                copy_ok = Namespace.CopyAlias(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "lists")
            {
                copy_ok = Namespace.CopyLists(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "timers")
            {
                copy_ok = Namespace.CopyTimers(namespace_src, namespace_dst, name_src, name_dst);
            }
            else
            {
                throw new RCEArgumentError(node, $"{cmd} items kind must be either: 'all', 'alias', 'lists', 'timers'  ");
            }
            return copy_ok;
        }

        /// <summary>
        /// targettype (graphic) [color] [range]
        /// </summary>
        private bool TargetType(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string command = node.Lexeme;
            // targettype (graphic) [color] [range]
            if (args.Length == 0) { WrongParameterCount(node, 1, 0); }
            var graphic = args[0].AsInt();
            var color = (args.Length >= 2 ? args[1].AsInt() : -1);
            var range = (args.Length >= 3 ? args[2].AsInt() : (int)Player.Backpack.Serial);

            Item itm = null;
            // Container (Range: Container Serial)
            itm = Items.FindByID(graphic, color, Player.Backpack.Serial, range);

            if (itm != null)
            {
                RazorEnhanced.Target.TargetExecute(itm);
                return true;
            }
            else
            {
                var options = new Items.Filter();
                options.CheckIgnoreObject = true;
                options.Graphics.Add(graphic);
                if (color != -1)
                    options.Hues.Add(color);
                options.RangeMin = -1;
                options.RangeMax = range;
                options.OnGround = 1;

                var item_list = Items.ApplyFilter(options);
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
            }

            if (itm == null)
            {
                Mobile mob = null;
                // Container (Range: Container Serial)
                var options = new Mobiles.Filter();
                options.CheckIgnoreObject = true;

                List<byte> notoriety = new List<byte>
                    {
                        (byte)1,
                        (byte)2,
                        (byte)3,
                        (byte)4,
                        (byte)5,
                        (byte)6,
                        (byte)7
                    };

                options.Bodies.Add(graphic);
                if (color != -1)
                    options.Hues.Add(color);
                options.RangeMin = -1;
                options.RangeMax = range;

                var mob_list = Mobiles.ApplyFilter(options);
                if (mob_list.Count > 0)
                {
                    mob_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    mob = mob_list[0];
                }

                if (mob == null)
                {
                    if (!quiet) { SendError("targettype: graphic " + graphic.ToString() + " not found in range " + range.ToString()); }
                }
                else
                {
                    RazorEnhanced.Target.TargetExecute(mob);
                    return true;
                }
            }
            else
            {
                RazorEnhanced.Target.TargetExecute(itm);
                return true;
            }

            return false;
        }

        /// <summary>
        /// targetground (graphic) [color] [range]
        /// </summary>
        private bool TargetGround(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // targettype (graphic) [color] [range]
            if (args.Length == 0) { WrongParameterCount(node, 1, 0); }
            var graphic = args[0].AsInt();
            var color = (args.Length >= 2 ? args[1].AsInt() : -1);
            var range = (args.Length >= 3 ? args[2].AsInt() : -1);

            Item itm = null;
            var options = new Items.Filter();
            options.CheckIgnoreObject = true;

            options.Graphics.Add(graphic);
            if (color != -1)
                options.Hues.Add(color);
            options.RangeMin = -1;
            options.RangeMax = range;
            options.OnGround = 1;


            var item_list = Items.ApplyFilter(options);
            if (item_list.Count > 0)
            {
                item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                itm = item_list[0];
            }


            if (itm == null)
            {
                if (!quiet)
                {
                    SendError("targettype: graphic " + graphic.ToString() + " not found in range " + range.ToString());
                }
                return false;
            }

            RazorEnhanced.Target.TargetExecute(itm);
            return true;
        }

        internal static int[] LastTileTarget = new int[4] { 0, 0, 0, 0 };

        /// <summary>
        ///  targettile ('last'/'current'/(x y z)) [graphic]
        /// </summary>
        private bool TargetTile(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1) { WrongParameterCount(node, 1, args.Length); }
            if (args.Length == 2 || args.Length == 4) // then graphic specified just use it
            {
                int graphic = 0;
                if (args.Length == 2)
                    graphic = args[1].AsInt();
                if (args.Length == 4)
                    graphic = args[3].AsInt();
                var options = new Items.Filter();
                options.CheckIgnoreObject = true;
                options.Graphics.Add(graphic);
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                Item itm = null;
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet)
                    {
                        SendError("targettile: graphic " + graphic.ToString() + " not found");
                    }
                    return false;
                }

                LastTileTarget[0] = itm.Position.X;
                LastTileTarget[1] = itm.Position.Y;
                LastTileTarget[2] = itm.Position.Z;
                var tiles2 = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
                if (tiles2.Count > 0)
                {
                    LastTileTarget[2] = tiles2[0].StaticZ;
                    LastTileTarget[3] = tiles2[0].StaticID;
                }
                RazorEnhanced.Target.TargetExecute(itm);
                return true;
            }
            // if we get here graphic wasnt specified


            if (args[0].AsString() == "current")
            {
                LastTileTarget[0] = Player.Position.X;
                LastTileTarget[1] = Player.Position.Y;
                LastTileTarget[2] = Player.Position.Z;
            }

            if (args.Length == 3)
            {
                LastTileTarget[0] = args[0].AsInt();
                LastTileTarget[1] = args[1].AsInt();
                LastTileTarget[2] = args[2].AsInt();
            }
            var tiles = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
            if (tiles.Count > 0)
            {
                LastTileTarget[2] = tiles[0].StaticZ;
                LastTileTarget[3] = tiles[0].StaticID;
            }

            RazorEnhanced.Target.TargetExecute(LastTileTarget[0], LastTileTarget[1], LastTileTarget[2], LastTileTarget[3]);
            return true;
        }

        /// <summary>
        /// targettileoffset (x y z) [graphic]
        /// </summary>
        private bool TargetTileOffset(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 3) { WrongParameterCount(node, 3, args.Length); }
            if (args.Length == 4) // then graphic specified just use it
            {
                int graphic = 0;
                if (args.Length == 4)
                    graphic = args[3].AsInt();
                var options = new Items.Filter();
                options.CheckIgnoreObject = true;
                options.Graphics.Add(graphic);
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                Item itm = null;
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet) { SendError("targettile: graphic " + graphic.ToString() + " not found"); }
                    return false;
                }

                LastTileTarget[0] = itm.Position.X;
                LastTileTarget[1] = itm.Position.Y;
                LastTileTarget[2] = itm.Position.Z;
                var tiles2 = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
                if (tiles2.Count > 0)
                {
                    LastTileTarget[2] = tiles2[0].StaticZ;
                    LastTileTarget[3] = tiles2[0].StaticID;
                }
                RazorEnhanced.Target.TargetExecute(itm);

                return true;
            }
            // if we get here graphic wasnt specified
            LastTileTarget[0] = Player.Position.X + args[0].AsInt();
            LastTileTarget[1] = Player.Position.Y + args[1].AsInt();
            LastTileTarget[2] = Player.Position.Z + args[2].AsInt();
            var tiles = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
            if (tiles.Count > 0)
            {
                LastTileTarget[2] = tiles[0].StaticZ;
                LastTileTarget[3] = tiles[0].StaticID;
            }

            RazorEnhanced.Target.TargetExecute(LastTileTarget[0], LastTileTarget[1], LastTileTarget[2], LastTileTarget[3]);
            return true;
        }

        /// <summary>
        /// targettilerelative (serial) (range) [reverse = 'true' or 'false'] [graphic]
        /// </summary>
        private bool TargetTileRelative(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // targettilerelative   (serial) (range) [reverse = 'true' or 'false'] [graphic]
            if (args.Length < 2) { WrongParameterCount(node, 2, args.Length); }
            uint serial = args[0].AsSerial();
            int range = args[1].AsInt();
            bool reverse = false;
            int graphic = -1;
            if (args.Length == 3)
            {
                try
                {
                    reverse = args[2].AsBool();
                }
                catch (RCERuntimeError)
                {
                    // Maybe it was a graphic
                    graphic = args[2].AsInt();
                }
            }
            if (args.Length == 4)
            {
                reverse = args[2].AsBool();
                graphic = args[3].AsInt();
            }

            Item itm = null;
            if (graphic != -1)
            {
                var options = new Items.Filter();
                options.CheckIgnoreObject = true;
                options.Graphics.Add(graphic);
                options.RangeMax = range;
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet)
                    {
                        SendError("targettilerelative: graphic " + graphic.ToString() + " not found in range " + range.ToString());
                    }
                    return false;
                }

                RazorEnhanced.Target.TargetExecute(itm);
                return true;
            }

            if (reverse)
                range = 0 - range;
            RazorEnhanced.Target.TargetExecuteRelative((int)serial, range);

            return true;
        }

        /// <summary>
        /// war (on/off)
        /// </summary>
        private static bool WarMode(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            // warmode on/off
            if (args.Length != 1)
            {
                WrongParameterCount(node, 1, args.Length);
            }
            bool onOff = args[0].AsBool();
            Player.SetWarMode(onOff);

            return true;
        }

        /// <summary>
        ///cleartargetqueue
        /// </summary>
        private static bool ClearTargetQueue(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            RazorEnhanced.Target.ClearQueue();
            return true;
        }

        /// <summary>
        /// shownames NOT IMPLEMENTED 
        /// </summary>
        /* shownames ['mobiles'/'corpses'] */
        private static bool ShowNames(ASTNode node, Argument[] args, bool quiet, bool force)
        {

            return NotImplemented(node, args, quiet, force);
        }



        #endregion


    }


    #region Parser/Interpreter




    internal static class TypeConverter
    {
        public static int ToInt(ASTNode node)
        {
            string token = node.Lexeme;
            int val;

            token = token.Replace("(", "").Replace(")", "");  // get rid of ( or ) if its there
            if (token.StartsWith("0x"))
            {
                if (int.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (int.TryParse(token, out val))
                return val;

            throw new RCERuntimeError(node, "Cannot convert argument to int");
        }

        public static uint ToUInt(ASTNode node)
        {
            string token = node.Lexeme;
            uint val;

            if (token.StartsWith("0x"))
            {
                if (uint.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (uint.TryParse(token, out val))
                return val;

            throw new RCERuntimeError(node, "Cannot convert " + token + " argument to uint");
        }

        public static ushort ToUShort(ASTNode node)
        {
            string token = node.Lexeme;
            ushort val;

            if (token.StartsWith("0x"))
            {
                if (ushort.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (ushort.TryParse(token, out val))
                return val;

            throw new RCERuntimeError(node, "Cannot convert argument to ushort");
        }

        public static double ToDouble(ASTNode node)
        {
            string token = node.Lexeme;
            double val;

            if (double.TryParse(token, out val))
                return val;

            throw new RCERuntimeError(node, "Cannot convert argument to double");
        }

        public static bool ToBool(ASTNode node)
        {
            string token = node.Lexeme;
            bool val;
            switch (token)
            {
                case "on":
                    token = "true";
                    break;
                case "off":
                    token = "false";
                    break;
            }
            if (bool.TryParse(token, out val))
                return val;

            throw new RCERuntimeError(node, "Cannot convert argument to bool");
        }
    }

    public class Scope
    {
        private readonly Dictionary<string, Argument> _namespace = new Dictionary<string, Argument>();

        public readonly ASTNode StartNode;
        public readonly Scope Parent;
        public System.Diagnostics.Stopwatch timer;

        public Scope(Scope parent, ASTNode start)
        {
            Parent = parent;
            StartNode = start;
            timer = new System.Diagnostics.Stopwatch();
        }

        public Argument GetVar(string name)
        {
            Argument arg;

            if (_namespace.TryGetValue(name, out arg))
                return arg;

            return null;
        }

        public void SetVar(string name, Argument val)
        {
            _namespace[name] = val;
        }

        public void ClearVar(string name)
        {
            _namespace.Remove(name);
        }
    }

    public class Argument
    {
        internal ASTNode Node
        {
            get; set;
        }

        internal RCECompiledScript _script
        {
            get; set;
        }

        public Argument(RCECompiledScript script, ASTNode node)
        {
            Node = node;
            _script = script;
        }

        // Treat the argument as an integer
        public int AsInt()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to int: {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsInt();

            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                int value = (int)_script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return value;
            }
            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsInt();
            return TypeConverter.ToInt(Node);
        }

        // Treat the argument as an unsigned integer
        public uint AsUInt()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to uint: {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsUInt();

            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                uint value = _script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return value;
            }

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsUInt();
            return TypeConverter.ToUInt(Node);
        }

        public ushort AsUShort()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to ushort {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsUShort();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsUShort();

            return TypeConverter.ToUShort(Node);
        }

        // Treat the argument as a serial or an alias. Aliases will
        // be automatically resolved to serial numbers.
        public uint AsSerial()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to serial {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsSerial();

            // Resolve it as a global alias next
            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                uint serial = _script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return serial;
            }

            try
            {
                arg = CheckIsListElement(Node.Lexeme);
                if (arg != null)
                    return arg.AsUInt();
                return AsUInt();
            }
            catch (RCERuntimeError)
            {
                // invalid numeric
            }
            try
            {
                arg = CheckIsListElement(Node.Lexeme);
                if (arg != null)
                    return (uint)arg.AsInt();
                return (uint)AsInt();
            }
            catch (RCERuntimeError)
            {
                // invalid numeric
            }
            // This is a bad place to be
            return 0;
        }

        // Treat the argument as a string
        public string AsString()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to string {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsString();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsString();

            return Node.Lexeme;
        }

        internal Argument CheckIsListElement(string token)
        {
            Regex rx = new Regex(@"(\S+)\[(\d+)\]");
            Match match = rx.Match(token);
            if (match.Success)
            {
                string list = match.Groups[1].Value;
                int index = int.Parse(match.Groups[2].Value);
                if (_script.Engine.Interpreter.ListExists(list))
                {
                    return _script.Engine.Interpreter.GetListValue(list, index);
                }
            }
            return null;
        }

        public bool AsBool()
        {
            if (Node.Lexeme == null)
                throw new RCERuntimeError(Node, $"Cannot convert argument to bool {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsBool();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsBool();


            return TypeConverter.ToBool(Node);
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            Argument arg = obj as Argument;

            if (arg == null)
                return false;

            return Equals(arg);
        }

        public bool Equals(Argument other)
        {
            if (other == null)
                return false;

            return (other.Node.Lexeme == Node.Lexeme);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
    public delegate bool RCETracebackDelegate(RCECompiledScript script, ASTNode node, Scope scope);
    public class RCECompiledScript
    {
        const int MinLoopTime = 100;
        bool Debug { get; set; }
        bool DebugTS { get; set; }

        public string Filename;
        public RazorCeEngine Engine;
        Action<string> DebugWriter;

        public event RCETracebackDelegate OnTraceback;

        private ASTNode _root;
        private ASTNode _statement;

        private Scope _scope;

        public RCECompiledScript()
        {
            Debug = false;
        }

        public Argument Lookup(string name)
        {
            var scope = _scope;
            Argument result = null;

            while (scope != null)
            {
                result = scope.GetVar(name);
                if (result != null)
                    return result;

                scope = scope.Parent;
            }

            return result;
        }

        private void PushScope(ASTNode node)
        {
            _scope = new Scope(_scope, node);
        }

        private void PopScope()
        {
            _scope = _scope.Parent;
        }

        internal Scope CurrentScope()
        {
            return _scope;
        }

        private Argument[] ConstructArguments(ref ASTNode node)
        {
            List<Argument> args = new List<Argument>();

            node = node.Next();

            bool comment = false;
            while (node != null)
            {
                switch (node.Type)
                {
                    case ASTNodeType.AND:
                    case ASTNodeType.OR:
                    case ASTNodeType.EQUAL:
                    case ASTNodeType.NOT_EQUAL:
                    case ASTNodeType.LESS_THAN:
                    case ASTNodeType.LESS_THAN_OR_EQUAL:
                    case ASTNodeType.GREATER_THAN:
                    case ASTNodeType.GREATER_THAN_OR_EQUAL:
                        return args.ToArray();
                }
                if (node.Lexeme == "//")
                    comment = true;

                if (!comment)
                    args.Add(new Argument(this, node));

                node = node.Next();
            }

            return args.ToArray();
        }

        // For now, the scripts execute directly from the
        // abstract syntax tree. This is relatively simple.
        // A more robust approach would be to "compile" the
        // scripts to a bytecode. That would allow more errors
        // to be caught with better error messages, as well as
        // make the scripts execute more quickly.
        public RCECompiledScript(ASTNode root, RazorCeEngine engine)
        {
            // Set current to the first statement
            _root = root;
            Engine = engine;
            Init();
        }

        public void Init()
        {
            _statement = _root.FirstChild();
            _scope = new Scope(null, _statement);

        }

        public bool ExecuteNext()
        {
            if (_statement == null)
            {
                Init();
            }
            if (_statement == null) { return false; }

            if (Debug)
            {
                if (_statement != null)
                {
                    string msg = String.Format("{0}: {1}", _statement.LineNumber + 1, _statement.Lexer.GetLine(_statement.LineNumber));
                    if (DebugTS)
                    {

                        var ts = DateTime.Now.ToString("mmss:fff");
                        msg = String.Format("{0} {1}: {2}", ts, _statement.LineNumber + 1, _statement.Lexer.GetLine(_statement.LineNumber));
                    }
                    Engine.SendOutput(msg);
                }
            }

            if (_statement.Type != ASTNodeType.STATEMENT)
                throw new RCERuntimeError(_statement, "Invalid script");

            var node = _statement.FirstChild();

            if (OnTraceback != null && !OnTraceback.Invoke(this, node, _scope))
            {
                return false;
            }

            if (node == null)
                throw new RCERuntimeError(_statement, "Invalid statement");



            int depth;
            switch (node.Type)
            {
                case ASTNodeType.IF:
                    {
                        PushScope(node);

                        var expr = node.FirstChild();
                        bool result = false;

                        result = EvaluateExpression(ref expr);

                        // Advance to next statement
                        Advance();

                        // Evaluated true. Jump right into execution.
                        if (result)
                            break;

                        // The expression evaluated false, so keep advancing until
                        // we hit an elseif, else, or endif statement that matches
                        // and try again.
                        depth = 0;

                        while (_statement != null)
                        {
                            node = _statement.FirstChild();

                            if (node.Type == ASTNodeType.IF)
                            {
                                depth++;
                            }
                            else if (node.Type == ASTNodeType.ELSEIF)
                            {
                                if (depth == 0)
                                {
                                    expr = node.FirstChild();
                                    result = EvaluateExpression(ref expr);

                                    // Evaluated true. Jump right into execution
                                    if (result)
                                    {
                                        Advance();
                                        break;
                                    }
                                }
                            }
                            else if (node.Type == ASTNodeType.ELSE)
                            {
                                if (depth == 0)
                                {
                                    // Jump into the else clause
                                    Advance();
                                    break;
                                }
                            }
                            else if (node.Type == ASTNodeType.ENDIF)
                            {
                                if (depth == 0)
                                    break;

                                depth--;
                            }

                            Advance();
                        }

                        if (_statement == null)
                            throw new RCERuntimeError(node, "If with no matching endif");

                        break;
                    }
                case ASTNodeType.ELSEIF:
                    // If we hit the elseif statement during normal advancing, skip over it. The only way
                    // to execute an elseif clause is to jump directly in from an if statement.
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.IF)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDIF)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        Advance();
                    }

                    if (_statement == null)
                        throw new RCERuntimeError(node, "If with no matching endif");

                    break;
                case ASTNodeType.ENDIF:
                    PopScope();
                    Advance();
                    break;
                case ASTNodeType.ELSE:
                    // If we hit the else statement during normal advancing, skip over it. The only way
                    // to execute an else clause is to jump directly in from an if statement.
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.IF)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDIF)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        Advance();
                    }

                    if (_statement == null)
                        throw new RCERuntimeError(node, "If with no matching endif");

                    break;
                case ASTNodeType.WHILE:
                    {
                        // When we first enter the loop, push a new scope
                        if (_scope.StartNode != node)
                        {
                            PushScope(node);
                        }
                        _scope.timer.Start();
                        var expr = node.FirstChild();
                        var result = EvaluateExpression(ref expr);

                        // Advance to next statement
                        Advance();

                        // The expression evaluated false, so keep advancing until
                        // we hit an endwhile statement.
                        if (!result)
                        {
                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.WHILE)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDWHILE)
                                {
                                    if (depth == 0)
                                    {
                                        int duration = (int)_scope.timer.ElapsedMilliseconds;
                                        if (duration < MinLoopTime)
                                            Misc.Pause(MinLoopTime - duration);
                                        _scope.timer.Reset();
                                        PopScope();
                                        // Go one past the endwhile so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }

                                    depth--;
                                }

                                Advance();
                            }
                        }
                        break;
                    }
                case ASTNodeType.ENDWHILE:
                    // Walk backward to the while statement
                    bool curDebug = Debug;
                    int whileStmnts = 0;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    depth = 0;

                    while (_statement != null)
                    {
                        whileStmnts++;
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDWHILE)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.WHILE)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        _statement = _statement.Prev();
                    }

                    var duration2 = _scope.timer.ElapsedMilliseconds;
                    if (duration2 < MinLoopTime)
                        Misc.Pause((int)(MinLoopTime - duration2));
                    _scope.timer.Reset();
                    Debug = curDebug;
                    if (_statement == null)
                        throw new RCERuntimeError(node, "Unexpected endwhile");

                    break;
                case ASTNodeType.FOR:
                    {
                        // The iterator variable's name is the hash code of the for loop's ASTNode.
                        var iterName = node.GetHashCode().ToString(); // + "_" + node.LineNumber.ToString();

                        // When we first enter the loop, push a new scope
                        if (_scope.StartNode != node)
                        {
                            PushScope(node);
                            _scope.timer.Start();
                            // Grab the arguments
                            var max = node.FirstChild();

                            if (max.Type != ASTNodeType.INTEGER)
                                throw new RCERuntimeError(max, "Invalid for loop syntax");

                            // Create a dummy argument that acts as our loop variable
                            var iter = new ASTNode(ASTNodeType.INTEGER, "0", node, 0);

                            _scope.SetVar(iterName, new Argument(this, iter));
                        }
                        else
                        {
                            // Increment the iterator argument
                            var arg = _scope.GetVar(iterName);

                            var iter = new ASTNode(ASTNodeType.INTEGER, (arg.AsUInt() + 1).ToString(), node, 0);

                            _scope.SetVar(iterName, new Argument(this, iter));
                        }

                        // Check loop condition
                        var i = _scope.GetVar(iterName);

                        // Grab the max value to iterate to
                        node = node.FirstChild();
                        var end = new Argument(this, node);

                        if (i.AsUInt() < end.AsUInt())
                        {
                            // enter the loop
                            Advance();
                        }
                        else
                        {
                            // Walk until the end of the loop
                            Advance();

                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDFOR)
                                {

                                    if (depth == 0)
                                    {
                                        PopScope();
                                        // Go one past the end so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }
                                    depth--;

                                }

                                Advance();
                            }
                        }
                    }
                    break;
                case ASTNodeType.FOREACH:
                    {
                        /*Dalamar
                        ORIGINAL RCE Behaviour:
                        All list iteration start from 0 and provide in list_name[] the first item of the list while in the forloop.
                        The start/end parameter simply "cap" the list size to a lower value as folloing:

                        for (start) to ('list name')
                        n_interations = list_length - start

                        for (start) to (end) in ('list name')
                        n_interations = ( end - start )

                        this second case falls back to the first.


                        PS: feels like writing intentionally broken code, but it's 100% RCEteam behaviour
                        */

                        // The iterator's name is the hash code of the for loop's ASTNode.
                        var children = node.Children();
                        ASTNode startParam = children[0];
                        ASTNode endParam = null;
                        ASTNode listParam;
                        if (children.Length == 2)
                        {
                            listParam = children[1];
                        }
                        else
                        {
                            endParam = children[1];
                            listParam = children[2];
                        }


                        string listName = listParam.Lexeme;
                        int listSize = Engine.Interpreter.ListLength(listName);

                        string iterName = listParam.GetHashCode().ToString(); //+"_"+ listParam.LineNumber.ToString();
                        string varName = listName + "[]";
                        int start = int.Parse(startParam.Lexeme);


                        int num_iter;
                        if (endParam == null)
                        {
                            num_iter = listSize - start;
                        }
                        else
                        {
                            int end = int.Parse(endParam.Lexeme) + 1; // +1 is important
                            if (end > listSize)
                            {
                                throw new RCERuntimeError(node, "Invalid for loop: END parameter must be smaller then the list size (" + listSize + "), " + (end - 1) + " given ");
                            }
                            num_iter = end - start;
                        }


                        if (num_iter > 0)
                        {

                            var idx = 0;
                            // When we first enter the loop, push a new scope
                            if (_scope.StartNode != node)
                            {
                                PushScope(node);
                                // Create a dummy argument that acts as our iterator object
                                var iter = new ASTNode(ASTNodeType.INTEGER, idx.ToString(), node, 0);
                                _scope.SetVar(iterName, new Argument(this, iter));

                                // Make the user-chosen variable have the value for the front of the list
                                var arg = Engine.Interpreter.GetListValue(listName, 0);

                                if (arg == null || 0 >= num_iter)
                                    _scope.ClearVar(varName);
                                else
                                    _scope.SetVar(varName, arg);
                            }
                            else
                            {
                                // Increment the iterator argument
                                idx = _scope.GetVar(iterName).AsInt() + 1;
                                var iter = new ASTNode(ASTNodeType.INTEGER, idx.ToString(), node, 0);
                                _scope.SetVar(iterName, new Argument(this, iter));

                                // Update the user-chosen variable
                                var arg = Engine.Interpreter.GetListValue(listName, idx);

                                if (arg == null || idx >= num_iter)
                                {
                                    _scope.ClearVar(varName);
                                }
                                else
                                {
                                    _scope.SetVar(varName, arg);
                                }
                            }
                        }
                        else
                        {
                            // nothing to do - force end
                            _scope.ClearVar(varName);
                        }

                        // Check loop condition
                        var i = _scope.GetVar(varName);

                        if (i != null)
                        {
                            // enter the loop
                            Advance();
                        }
                        else
                        {
                            // Walk until the end of the loop
                            Advance();

                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDFOR)
                                {
                                    if (depth == 0)
                                    {
                                        PopScope();
                                        // Go one past the end so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }

                                    depth--;
                                }

                                Advance();
                            }
                        }
                        break;
                    }
                case ASTNodeType.ENDFOR:
                    // Walk backward to the for statement
                    // track depth in case there is a nested for
                    // Walk backward to the for statement
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    // track depth in case there is a nested for
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDFOR)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                        {
                            if (depth == 0)
                            {
                                break;
                            }
                            depth--;
                        }

                        _statement = _statement.Prev();

                    }
                    Debug = curDebug;

                    if (_statement == null)
                        throw new RCERuntimeError(node, "Unexpected endfor");
                    break;
                case ASTNodeType.BREAK:
                    // Walk until the end of the loop
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement
                    Advance();

                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.WHILE ||
                            node.Type == ASTNodeType.FOR ||
                            node.Type == ASTNodeType.FOREACH)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDWHILE ||
                            node.Type == ASTNodeType.ENDFOR)
                        {
                            if (depth == 0)
                            {
                                PopScope();

                                // Go one past the end so the loop doesn't repeat
                                Advance();
                                break;
                            }

                            depth--;
                        }

                        Advance();
                    }

                    PopScope();
                    Debug = curDebug;
                    break;
                case ASTNodeType.CONTINUE:
                    // Walk backward to the loop statement
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDWHILE ||
                            node.Type == ASTNodeType.ENDFOR)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.WHILE ||
                                    node.Type == ASTNodeType.FOR ||
                                    node.Type == ASTNodeType.FOREACH)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        _statement = _statement.Prev();
                    }
                    Debug = curDebug;
                    if (_statement == null)
                        throw new RCERuntimeError(node, "Unexpected continue");
                    break;
                case ASTNodeType.STOP:
                    Engine.Interpreter.StopScript();
                    _statement = null;
                    throw new RCEStopError(node, "Found stop keyword.");
                    break;
                case ASTNodeType.REPLAY:
                    _statement = _statement.Parent.FirstChild();
                    break;
                case ASTNodeType.QUIET:
                case ASTNodeType.FORCE:
                case ASTNodeType.COMMAND:
                    ExecuteCommand(node);
                    Advance();

                    break;
            }
            return (_statement != null);
        }

        public void Advance()
        {
            Engine.Interpreter.ClearTimeout();
            _statement = _statement.Next();

        }

        private ASTNode EvaluateModifiers(ASTNode node, out bool quiet, out bool force, out bool not)
        {
            quiet = false;
            force = false;
            not = false;

            while (true)
            {
                switch (node.Type)
                {
                    case ASTNodeType.QUIET:
                        quiet = true;
                        break;
                    case ASTNodeType.FORCE:
                        force = true;
                        break;
                    case ASTNodeType.NOT:
                        not = true;
                        break;
                    default:
                        return node;
                }

                node = node.Next();
            }
        }

        private bool ExecuteCommand(ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out bool force, out _);

            var cont = true;
            if (node.Lexeme.ToLower() == "debug")
            {
                var args = ConstructArguments(ref node);
                if (args.Length == 1)
                {
                    string debugArgument = args[0].AsString().ToLower();
                    if (debugArgument == "on"
                        || debugArgument == "ts")
                        Debug = true;
                    else
                    {
                        Debug = false;
                        DebugTS = false;
                    }
                    if (debugArgument == "ts")
                        DebugTS = true;
                    else
                        DebugTS = false;
                }
                cont = true;
            }
            else
            {
                var handler = Engine.Interpreter.GetCommandHandler(node.Lexeme);
                if (handler == null)
                    throw new RCESyntaxError(node, "Unknown command");

                cont = handler(node, ConstructArguments(ref node), quiet, force);

                if (node != null)
                    throw new RCERuntimeError(node, "Command did not consume all available arguments");
            }
            return cont;
        }

        private bool EvaluateExpression(ref ASTNode expr)
        {
            if (expr == null || (expr.Type != ASTNodeType.UNARY_EXPRESSION && expr.Type != ASTNodeType.BINARY_EXPRESSION && expr.Type != ASTNodeType.LOGICAL_EXPRESSION))
                throw new RCERuntimeError(expr, "No expression following control statement");

            var node = expr.FirstChild();

            if (node == null)
                throw new RCERuntimeError(expr, "Empty expression following control statement");

            switch (expr.Type)
            {
                case ASTNodeType.UNARY_EXPRESSION:
                    return EvaluateUnaryExpression(ref node);

                case ASTNodeType.BINARY_EXPRESSION:
                    return EvaluateBinaryExpression(ref node);
            }

            bool lhs = EvaluateExpression(ref node);

            node = node.Next();

            while (node != null)
            {
                // Capture the operator
                var op = node.Type;
                node = node.Next();

                if (node == null)
                    throw new RCERuntimeError(node, "Invalid logical expression");

                // short circuit the if/and if lhs is already false with and, already true with or 
                if (op == ASTNodeType.AND && lhs == false)
                    return lhs;
                if (op == ASTNodeType.OR && lhs == true)
                    return lhs;

                bool rhs;
                var e = node.FirstChild();
                switch (node.Type)
                {
                    case ASTNodeType.UNARY_EXPRESSION:
                        rhs = EvaluateUnaryExpression(ref e);
                        break;
                    case ASTNodeType.BINARY_EXPRESSION:
                        rhs = EvaluateBinaryExpression(ref e);
                        break;
                    default:
                        throw new RCERuntimeError(node, "Nested logical expressions are not possible");
                }

                switch (op)
                {
                    case ASTNodeType.AND:
                        lhs = lhs && rhs;
                        break;
                    case ASTNodeType.OR:
                        lhs = lhs || rhs;
                        break;
                    default:
                        throw new RCERuntimeError(node, "Invalid logical operator");
                }

                node = node.Next();
            }

            return lhs;
        }

        private bool CompareOperands(ASTNodeType op, IComparable lhs, IComparable rhs)
        {
            if (lhs.GetType() != rhs.GetType())
            {
                // Different types. Try to convert one to match the other.

                if (rhs is double)
                {
                    // Special case for rhs doubles because we don't want to lose precision.
                    lhs = (double)lhs;
                }
                else if (rhs is bool)
                {
                    // Special case for rhs bools because we want to down-convert the lhs.
                    var tmp = Convert.ChangeType(lhs, typeof(bool));
                    lhs = (IComparable)tmp;
                }
                else
                {
                    var tmp = Convert.ChangeType(rhs, lhs.GetType());
                    rhs = (IComparable)tmp;
                }
            }

            try
            {
                // Evaluate the whole expression
                switch (op)
                {
                    case ASTNodeType.EQUAL:
                        return lhs.CompareTo(rhs) == 0;
                    case ASTNodeType.NOT_EQUAL:
                        return lhs.CompareTo(rhs) != 0;
                    case ASTNodeType.LESS_THAN:
                        return lhs.CompareTo(rhs) < 0;
                    case ASTNodeType.LESS_THAN_OR_EQUAL:
                        return lhs.CompareTo(rhs) <= 0;
                    case ASTNodeType.GREATER_THAN:
                        return lhs.CompareTo(rhs) > 0;
                    case ASTNodeType.GREATER_THAN_OR_EQUAL:
                        return lhs.CompareTo(rhs) >= 0;
                }
            }
            catch (ArgumentException e)
            {
                throw new RCERuntimeError(_statement, e.Message);
            }

            throw new RCERuntimeError(_statement, $"Unknown operator '{op}' in expression");

        }

        private bool EvaluateUnaryExpression(ref ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out _, out bool ifnot);

            var handler = Engine.Interpreter.GetExpressionHandler(node.Lexeme);

            if (handler == null)
                throw new RCERuntimeError(node, "Unknown expression");

            var result = handler(node, ConstructArguments(ref node), quiet);

            if (ifnot)
                return CompareOperands(ASTNodeType.EQUAL, result, false);
            else
                return CompareOperands(ASTNodeType.EQUAL, result, true);
        }

        private bool EvaluateBinaryExpression(ref ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out _, out bool ifnot);

            // Evaluate the left hand side
            var lhs = EvaluateBinaryOperand(ref node);

            // Capture the operator
            var op = node.Type;
            node = node.Next();

            // Evaluate the right hand side
            var rhs = EvaluateBinaryOperand(ref node);

            var result = CompareOperands(op, lhs, rhs);
            if (ifnot)
                return CompareOperands(ASTNodeType.EQUAL, result, false);
            else
                return CompareOperands(ASTNodeType.EQUAL, result, true);
        }

        private IComparable EvaluateBinaryOperand(ref ASTNode node)
        {
            IComparable val;

            node = EvaluateModifiers(node, out bool quiet, out _, out _);
            switch (node.Type)
            {
                case ASTNodeType.INTEGER:
                    val = TypeConverter.ToInt(node);
                    break;
                case ASTNodeType.SERIAL:
                    val = TypeConverter.ToUInt(node);
                    break;
                case ASTNodeType.STRING:
                    val = node.Lexeme;
                    break;
                case ASTNodeType.DOUBLE:
                    val = TypeConverter.ToDouble(node);
                    break;
                case ASTNodeType.OPERAND:
                    {
                        // This might be a registered keyword, so do a lookup
                        var handler = Engine.Interpreter.GetExpressionHandler(node.Lexeme);
                        if (handler != null)
                            val = handler(node, ConstructArguments(ref node), quiet);
                        else
                        {
                            Argument temp = new Argument(this, node);
                            val = temp.AsString();
                        }
                        break;
                    }
                default:
                    throw new RCERuntimeError(node, "Invalid type found in expression");
            }

            return val;
        }
    }

    public class Namespace
    {
        const string DEFAULT_NAMESPACE = "global";

        public static readonly ConcurrentDictionary<string, Namespace> _namespaces = new ConcurrentDictionary<string, Namespace>();
        public static readonly Namespace GlobalNamespace = new Namespace(DEFAULT_NAMESPACE);

        // Timers
        public readonly string Name;
        public readonly ConcurrentDictionary<string, int> _alias = new ConcurrentDictionary<string, int>();
        public readonly ConcurrentDictionary<string, DateTime> _timers = new ConcurrentDictionary<string, DateTime>();
        public readonly ConcurrentDictionary<string, List<Argument>> _lists = new ConcurrentDictionary<string, List<Argument>>();

        public static Namespace Get(string name = null)
        {
            if (name == null) { name = DEFAULT_NAMESPACE; }
            if (Has(name))
            {
                return _namespaces[name];
            }
            else
            {
                var ns = new Namespace(name);
                return ns;
            }
        }

        public static List<string> List()
        {
            return _namespaces.Keys.ToList();
        }

        public static bool Has(string name)
        {
            if (name == Namespace.GlobalNamespace.Name) return true;
            return _namespaces.ContainsKey(name);
        }

        public static void Delete(string name = null)
        {
            if (name == null) { name = DEFAULT_NAMESPACE; }

            if (name == Namespace.GlobalNamespace.Name)
            {
                Misc.SharedScriptData.Clear();
                GlobalNamespace._alias.Clear();
                GlobalNamespace._lists.Clear();
                GlobalNamespace._timers.Clear();
            }
            else if (Has(name))
            {
                _namespaces.TryRemove(name, out var _);
            }
        }

        public static string PrintAll(string namespace_name = null, string item_name = null)
        {
            var nss = namespace_name == null ? _namespaces.Keys.ToList() : new List<string> { namespace_name };

            var content = "";
            foreach (var ns in nss)
            {
                var contentAlias = PrintAlias(namespace_name, item_name);
                var contentLists = PrintLists(namespace_name, item_name);
                var contentTimers = PrintTimers(namespace_name, item_name);
                content += contentAlias + contentLists + contentTimers;
            }

            return content;
        }
        public static string PrintAlias(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._alias.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:alias:{pair.Key} = {pair.Value}\n"; }
                if (content == "") { content = $"{namespace_name}:alias: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._alias.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:alias:{item_name}{(found ? $" = {value}" : " NOT found")}\n";
            }

            return content;
        }

        public static string PrintLists(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._lists.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:lists:{pair.Key} = {string.Join(", ", pair.Value.Apply((a) => a.AsString()))}\n"; }
                if (content == "") { content = $"{namespace_name}:lists: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._lists.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:lists:{item_name} {(found ? $" = {value}" : "NOT found.")}\n";
            }
            return content;
        }

        public static string PrintTimers(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._timers.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:timers:{pair.Key} = {pair.Value}\n"; }
                if (content == "") { content = $"{namespace_name}:timers: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._timers.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:timers:{item_name} {(found ? $" = {value}" : "NOT found.")}\n";
            }
            return content;
        }

        public static bool CopyAll(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            var alias_ok = CopyAlias(namespace_src, namespace_dst, name_src, name_dst);
            var lists_ok = CopyLists(namespace_src, namespace_dst, name_src, name_dst);
            var timers_ok = CopyTimers(namespace_src, namespace_dst, name_src, name_dst);
            return alias_ok && lists_ok && timers_ok;
        }

        public static bool CopyAlias(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._alias.Concat(dst_ns._alias);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._alias.TryAdd(name_dst, dst_ns._alias[name_src]);
            }
            return true;
        }

        public static bool CopyLists(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._lists.Concat(dst_ns._lists);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._lists.TryAdd(name_dst, dst_ns._lists[name_src]);
            }
            return true;
        }

        public static bool CopyTimers(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._timers.Concat(dst_ns._timers);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._timers.TryAdd(name_dst, dst_ns._timers[name_src]);
            }
            return true;
        }

        public static bool Move(string oldname, string newname, bool replace = false)
        {
            if (!Has(oldname)) { return false; }

            if (replace)
            {
                Delete(newname);
                var new_ns = Get(newname);
                var old_ns = _namespaces[oldname];
            }
            CopyAll(oldname, newname);
            Delete(oldname);

            return true;
        }

        private Namespace(string name)
        {
            Name = name;
            _namespaces.TryAdd(name, this);
        }

    }

    public class Interpreter
    {
        internal static readonly Dictionary<string, AliasHandler> _aliasHandlers = new Dictionary<string, AliasHandler>();

        // Delegates
        //public delegate T ExpressionHandler<T>(string expression, Argument[] args, bool quiet) where T : IComparable;
        public delegate IComparable ExpressionHandler(ASTNode node, Argument[] args, bool quiet);
        public delegate bool CommandHandler(ASTNode node, Argument[] args, bool quiet, bool force);
        public delegate uint AliasHandler(string alias);

        internal readonly ConcurrentDictionary<string, ExpressionHandler> _exprHandlers = new ConcurrentDictionary<string, ExpressionHandler>();
        internal readonly ConcurrentDictionary<string, CommandHandler> _commandHandlers = new ConcurrentDictionary<string, CommandHandler>();

        // Timers

        internal ConcurrentDictionary<string, int> _alias { get { return m_Engine.Namespace._alias; } }
        internal ConcurrentDictionary<string, DateTime> _timers { get { return m_Engine.Namespace._timers; } }
        internal ConcurrentDictionary<string, List<Argument>> _lists { get { return m_Engine.Namespace._lists; } }


        // Lists
        private RazorCeEngine m_Engine;
        private RCECompiledScript m_Script = null;

        private bool m_Suspended = false;
        private ManualResetEvent m_SuspendedMutex;


        private enum ExecutionState
        {
            RUNNING,
            PAUSED,
            SUSPENDED,
            TIMING_OUT
        };

        public delegate bool TimeoutCallback();

        private ExecutionState _executionState = ExecutionState.RUNNING;
        private static long _pauseTimeout = long.MaxValue;
        private static TimeoutCallback _timeoutCallback = null;

        public static System.Globalization.CultureInfo Culture;

        static Interpreter()
        {
            Culture = new System.Globalization.CultureInfo(System.Globalization.CultureInfo.CurrentCulture.LCID, false);
            Culture.NumberFormat.NumberDecimalSeparator = ".";
            Culture.NumberFormat.NumberGroupSeparator = ",";
        }

        public Interpreter(RazorCeEngine engine)
        {
            m_Engine = engine;
            m_SuspendedMutex = new ManualResetEvent(!m_Suspended);
        }

        /// <summary>
        /// An adapter that lets expressions be registered as commands
        /// </summary>
        /// <param name="node">name of command</param>
        /// <param name="args">arguments passed to command</param>
        /// <param name="quiet">ignored</param>
        /// <param name="force">ignored</param>
        /// <returns></returns>
        private bool ExpressionCommand(ASTNode node, Argument[] args, bool quiet, bool force)
        {
            string command = node.Lexeme;
            var handler = GetExpressionHandler(command);
            handler(node, args, false);
            return true;
        }

        public void RegisterExpressionHandler(string keyword, ExpressionHandler handler)
        {
            //_exprHandlers[keyword] = (expression, args, quiet) => handler(expression, args, quiet);
            _exprHandlers[keyword] = handler;
            RegisterCommandHandler(keyword, ExpressionCommand); // also register expressions as commands
        }

        public ExpressionHandler GetExpressionHandler(string keyword)
        {
            _exprHandlers.TryGetValue(keyword, out ExpressionHandler expression);
            return expression;
        }

        public void RegisterCommandHandler(string keyword, CommandHandler handler, string docString = "default")
        {
            _commandHandlers[keyword] = handler;
            DocItem di = new DocItem("", "RazorCeEngine", keyword, docString);
        }
        public CommandHandler GetCommandHandler(string keyword)
        {
            _commandHandlers.TryGetValue(keyword, out CommandHandler handler);

            return handler;
        }

        public void RegisterAliasHandler(string keyword, AliasHandler handler)
        {
            _aliasHandlers[keyword] = handler;
        }

        public void UnregisterAliasHandler(string keyword)
        {
            _aliasHandlers.Remove(keyword);
        }

        public uint GetAlias(string alias)
        {
            // If a handler is explicitly registered, call that.
            if (_aliasHandlers.TryGetValue(alias, out AliasHandler handler))
                return handler(alias);

            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                // uint value;
                if (Misc.CheckSharedValue(alias))
                {
                    return (uint)Misc.ReadSharedValue(alias);
                }
            }
            if (_alias.TryGetValue(alias, out int value))
            {
                return (uint)value;
            }

            return uint.MaxValue;
        }
        public bool FindAlias(string alias)
        {
            // If a handler is explicitly registered, call that.

            if (_aliasHandlers.TryGetValue(alias, out AliasHandler handler))
                return true;
            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                return Misc.CheckSharedValue(alias);
            }
            else
            {
                return _alias.ContainsKey(alias);
            }
        }

        public void UnSetAlias(string alias)
        {

            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                Misc.RemoveSharedValue(alias);
            }
            if (_alias.ContainsKey(alias))
            {
                _alias.TryRemove(alias, out var _);
            }

        }
        public void SetAlias(string alias, uint serial)
        {
            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                Misc.SetSharedValue(alias, serial);
            }
            _alias.TryAdd(alias, (int)serial);
        }

        public void CreateList(string name)
        {
            if (_lists.ContainsKey(name))
                return;

            _lists[name] = new List<Argument>();
        }

        public void DestroyList(string name)
        {
            _lists.TryRemove(name, out var _);
        }

        public void ClearList(string name)
        {
            if (!_lists.ContainsKey(name))
                return;

            _lists[name].Clear();
        }

        public bool ListExists(string name)
        {
            return _lists.ContainsKey(name);
        }

        public bool ListContains(string name, Argument arg)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("ListContains {0} does not exist", name));

            return _lists[name].Contains(arg);
        }

        public List<Argument> ListContents(string name)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("ListContents {0} does not exist", name));

            return _lists[name];
        }


        public int ListLength(string name)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("ListLength {0} does not exist", name));

            return _lists[name].Count;
        }

        public void PushList(string name, Argument arg, bool front, bool unique)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("PushList {0} does not exist", name));

            if (unique && _lists[name].Contains(arg))
                return;

            if (front)
                _lists[name].Insert(0, arg);
            else
                _lists[name].Add(arg);
        }

        public bool PopList(string name, Argument arg)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("PopList {0} does not exist", name));

            return _lists[name].Remove(arg);
        }

        public bool PopList(string name, bool front)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("PopList {0} does not exist", name));

            var idx = front ? 0 : _lists[name].Count - 1;
            _lists[name].RemoveAt(idx);

            return _lists[name].Count > 0;
        }

        public static bool PopList(string name, bool front, out Variable removedVar)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError("List does not exist");

            var list = _lists[name];
            var idx = front ? 0 : _lists[name].Count - 1;

            removedVar = list[idx];
            list.RemoveAt(idx);

            return list.Count > 0;
        }
        public Argument GetListValue(string name, int idx)
        {
            if (!_lists.ContainsKey(name))
                throw new RCERuntimeError(null, String.Format("GetListValue {0} does not exist", name));

            var list = _lists[name];

            if (idx < list.Count)
                return list[idx];

            return null;
        }

        public void CreateTimer(string name)
        {
            _timers[name] = DateTime.UtcNow;
        }

        public TimeSpan GetTimer(string name)
        {
            if (!_timers.TryGetValue(name, out DateTime timestamp))
                throw new RCERuntimeError(null, String.Format("GetTimer {0} does not exist", name));

            TimeSpan elapsed = DateTime.UtcNow - timestamp;

            return elapsed;
        }

        public void SetTimer(string name, int elapsed)
        {
            // Setting a timer to start at a given value is equivalent to
            // starting the timer that number of milliseconds in the past.
            _timers[name] = DateTime.UtcNow.AddMilliseconds(-elapsed);
        }

        public void RemoveTimer(string name)
        {
            _timers.TryRemove(name, out var _);
        }

        public bool TimerExists(string name)
        {
            return _timers.ContainsKey(name);
        }


        public bool Suspended
        {
            get { return m_Suspended; }
            set
            {
                if (value) { SuspendScript(); }
                else { ReseumeScript(); }
            }
        }

        public void SuspendScript()
        {
            m_SuspendedMutex.Reset();
            m_Suspended = true;
        }

        public void ReseumeScript()
        {
            m_SuspendedMutex.Set();
            m_Suspended = false;
        }

        public bool StartScript()
        {
            if (m_Script != null)
                return false;

            m_Script = m_Engine.Compiled;
            _executionState = ExecutionState.RUNNING;
            Suspended = false;
            m_Script.Init();
            ExecuteScript();

            return true;
        }

        public void StopScript()
        {
            m_Script = null;
            Suspended = false;
            _executionState = ExecutionState.RUNNING;
        }

        public bool ExecuteScript()
        {
            if (m_Script == null)
                return false;

            if (_executionState == ExecutionState.PAUSED)
            {
                if (_pauseTimeout < DateTime.UtcNow.Ticks)
                    _executionState = ExecutionState.RUNNING;
                else
                    return true;
            }
            else if (_executionState == ExecutionState.TIMING_OUT)
            {
                if (_pauseTimeout < DateTime.UtcNow.Ticks)
                {
                    if (_timeoutCallback != null)
                    {
                        if (_timeoutCallback())
                        {
                            m_Script.Advance();
                            ClearTimeout();
                        }

                        _timeoutCallback = null;
                    }

                    /* If the callback changed the state to running, continue
                        * on. Otherwise, exit.
                        */
                    if (_executionState != ExecutionState.RUNNING)
                    {
                        m_Script = null;
                        return false;
                    }
                }
            }

            if (!m_Script.ExecuteNext())
            {
                m_Script = null;
                return false;
            }

            return true;
        }



        // Pause execution for the given number of milliseconds
        public void Pause(long duration)
        {
            // Already paused or timing out
            if (_executionState != ExecutionState.RUNNING)
                return;

            _pauseTimeout = DateTime.UtcNow.Ticks + (duration * 10000);
            _executionState = ExecutionState.PAUSED;
        }

        // Unpause execution
        public void Unpause()
        {
            if (_executionState != ExecutionState.PAUSED)
                return;

            _pauseTimeout = 0;
            _executionState = ExecutionState.RUNNING;
        }

        // If forward progress on the script isn't made within this
        // amount of time (milliseconds), bail
        public void Timeout(long duration, TimeoutCallback callback)
        {
            // Don't change an existing timeout
            if (_executionState != ExecutionState.RUNNING)
                return;

            _pauseTimeout = DateTime.UtcNow.Ticks + (duration * 10000);
            _executionState = ExecutionState.TIMING_OUT;
            _timeoutCallback = callback;
        }

        // Clears any previously set timeout. Automatically
        // called any time the script advances a statement.
        public void ClearTimeout()
        {
            if (_executionState != ExecutionState.TIMING_OUT)
                return;

            _pauseTimeout = 0;
            _executionState = ExecutionState.RUNNING;
        }

        public class Variable
        {
            private string _value;

            public Variable(string value)
            {
                _value = value;
            }

            // Treat the argument as an integer
            public int AsInt()
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to int");

                // Try to resolve it as a scoped variable first
                var arg = Interpreter.GetVariable(_value);
                if (arg != null)
                    return arg.AsInt();

                return TypeConverter.ToInt(_value);
            }

            // Treat the argument as an unsigned integer
            public uint AsUInt()
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to uint");

                // Try to resolve it as a scoped variable first
                var arg = Interpreter.GetVariable(_value);
                if (arg != null)
                    return arg.AsUInt();

                return TypeConverter.ToUInt(_value);
            }

            public ushort AsUShort()
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to ushort");

                // Try to resolve it as a scoped variable first
                var arg = Interpreter.GetVariable(_value);
                if (arg != null)
                    return arg.AsUShort();

                return TypeConverter.ToUShort(_value);
            }

            // Treat the argument as a serial or an alias. Aliases will
            // be automatically resolved to serial numbers.
            public uint AsSerial()
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to serial");

                // Try to resolve it as a scoped variable first
                var arg = Interpreter.GetVariable(_value);
                if (arg != null)
                    return arg.AsSerial();

                // Resolve it as a global alias next
                uint serial = Interpreter.GetAlias(_value);
                if (serial != uint.MaxValue)
                    return serial;

                try
                {
                    return AsUInt();
                }
                catch (RunTimeError)
                { }

                return Serial.MinusOne;
            }

            // Treat the argument as a string
            public string AsString(bool resolve = true)
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to string");

                if (resolve)
                {
                    // Try to resolve it as a scoped variable first
                    var arg = Interpreter.GetVariable(_value);
                    if (arg != null)
                        return arg.AsString();
                }

                return _value;
            }

            public bool AsBool()
            {
                if (_value == null)
                    throw new RunTimeError("Cannot convert argument to bool");

                return TypeConverter.ToBool(_value);
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                    return false;

                Variable arg = obj as Variable;

                if (arg == null)
                    return false;

                return Equals(arg);
            }

            public bool Equals(Variable other)
            {
                if (other == null)
                    return false;

                return (other._value == _value);
            }
        }

        public static Variable GetVariable(string name)
        {
            var scope = _currentScope;
            Variable result = null;

            while (scope != null)
            {
                result = scope.GetVariable(name);
                if (result != null)
                    return result;

                scope = scope.Parent;
            }

            return result;
        }

        public static void ClearVariable(string name)
        {
            _currentScope.ClearVariable(name);
        }

        public static bool ExistVariable(string name)
        {
            return _currentScope.ExistVariable(name);
        }
    }



    public enum ASTNodeType
    {
        // Keywords
        IF,
        ELSEIF,
        ELSE,
        ENDIF,
        WHILE,
        ENDWHILE,
        FOR,
        FOREACH,
        ENDFOR,
        BREAK,
        CONTINUE,
        STOP,
        REPLAY,

        // Operators
        EQUAL,
        NOT_EQUAL,
        LESS_THAN,
        LESS_THAN_OR_EQUAL,
        GREATER_THAN,
        GREATER_THAN_OR_EQUAL,

        // Logical Operators
        NOT,
        AND,
        OR,

        // Value types
        STRING,
        SERIAL,
        INTEGER,
        DOUBLE,
        LIST,

        // Modifiers
        QUIET, // @ symbol
        FORCE, // ! symbol

        // Everything else
        SCRIPT,
        STATEMENT,
        COMMAND,
        OPERAND,
        LOGICAL_EXPRESSION,
        UNARY_EXPRESSION,
        BINARY_EXPRESSION,
    }

    // Abstract Syntax Tree Node
    public class ASTNode
    {
        public readonly ASTNodeType Type;
        public readonly string Lexeme;
        public readonly ASTNode Parent;
        public readonly int LineNumber;
        public readonly Lexer Lexer;

        internal LinkedListNode<ASTNode> _node;
        private LinkedList<ASTNode> _children;

        public ASTNode(ASTNodeType type, string lexeme, ASTNode parent, int lineNumber, Lexer lexer = null)
        {
            Type = type;
            if (lexeme != null)
                Lexeme = lexeme;
            else
                Lexeme = "";
            Parent = parent;
            LineNumber = lineNumber;
            if (lexer == null && parent != null && parent.Lexer != null)
            {
                lexer = parent.Lexer;
            }
            this.Lexer = lexer;
        }

        public ASTNode Push(ASTNodeType type, string lexeme, int lineNumber)
        {
            var node = new ASTNode(type, lexeme, this, lineNumber);

            if (_children == null)
                _children = new LinkedList<ASTNode>();

            node._node = _children.AddLast(node);

            return node;
        }

        public ASTNode FirstChild()
        {
            if (_children == null || _children.First == null)
                return null;

            return _children.First.Value;
        }

        public ASTNode[] Children()
        {
            if (_children == null || _children.First == null)
                return null;

            return _children.ToArray();
        }

        public ASTNode Next()
        {
            if (_node == null || _node.Next == null)
                return null;

            return _node.Next.Value;
        }

        public ASTNode Prev()
        {
            if (_node == null || _node.Previous == null)
                return null;

            return _node.Previous.Value;
        }
    }


    //TODO: convert to "non static"  ( generic methos Slice gives issue, would need rewrite, but later down the roadmap )
    public static class LexerExtension
    {
        public static T[] Slice<T>(this T[] src, int start, int end)
        {
            if (end < start)
                return new T[0];

            int len = end - start + 1;

            T[] slice = new T[len];
            for (int i = 0; i < len; i++)
            {
                slice[i] = src[i + start];
            }

            return slice;
        }
    }

    public class Lexer
    {
        private int _curLine = 0;
        private string[] _lines;
        private ASTNode m_LastStatement;

        static Regex matchListName = new Regex("[a-zA-Z]+", RegexOptions.Compiled);
        static Regex matchNumber = new Regex("^[0-9]+$", RegexOptions.Compiled);


        //private static string _filename = ""; // can be empty

        public Lexer()
        {
        }

        public string GetLine(int lineNum = -1)
        {
            if (lineNum < 0) lineNum = _curLine;
            if (lineNum >= _lines.Length)
                return "line number exceeds all line numbers";
            return _lines[lineNum];
        }

        public ASTNode Lex(string[] lines)
        {
            _lines = lines;
            ASTNode root_node = new ASTNode(ASTNodeType.SCRIPT, null, null, 0, this);
            m_LastStatement = root_node;
            try
            {
                for (_curLine = 0; _curLine < lines.Length; _curLine++)
                {
                    foreach (var l in lines[_curLine].Split(';'))
                    {
                        ParseLine(root_node, l);
                    }
                }
            }
            catch (RCESyntaxError e)
            {
                throw new RCESyntaxError(m_LastStatement, e.Message);
                //throw new RCESyntaxError(lines[_curLine], _curLine, e.Node, "Syntax error!");
            }
            catch (Exception e)
            {
                throw new RCESyntaxError(m_LastStatement, e.Message);
            }

            return root_node;
        }

        public ASTNode Lex(string fname)
        {
            var lines = System.IO.File.ReadAllLines(fname);
            return Lex(lines);
        }

        private static readonly TextParser _tfp = new TextParser("", new char[] { ' ' }, new string[] { "//", "#" }, new char[] { '\'', '\'', '"', '"' });
        private void ParseLine(ASTNode node, string line)
        {
            line = line.Trim();

            if (line.StartsWith("//") || line.StartsWith("#"))
                return;

            // Split the line by spaces (unless the space is in quotes)
            var lexemes = _tfp.GetTokens(line, false);

            if (lexemes.Length == 0)
                return;

            ParseStatement(node, lexemes);
        }

        private void ParseValue(ASTNode node, string lexeme, ASTNodeType typeDefault)
        {
            if (lexeme.StartsWith("0x"))
                node.Push(ASTNodeType.SERIAL, lexeme, _curLine);
            else if (int.TryParse(lexeme, out _))
                node.Push(ASTNodeType.INTEGER, lexeme, _curLine);
            else if (double.TryParse(lexeme, out _))
                node.Push(ASTNodeType.DOUBLE, lexeme, _curLine);
            else
                node.Push(typeDefault, lexeme, _curLine);
        }

        private void ParseCommand(ASTNode node, string lexeme)
        {
            // A command may start with an '@' symbol. Pick that
            // off.
            if (lexeme[0] == '@')
            {
                node.Push(ASTNodeType.QUIET, null, _curLine);
                lexeme = lexeme.Substring(1, lexeme.Length - 1);
            }

            // A command may end with a '!' symbol. Pick that
            // off.
            if (lexeme.EndsWith("!"))
            {
                node.Push(ASTNodeType.FORCE, null, _curLine);
                lexeme = lexeme.Substring(0, lexeme.Length - 1);
            }

            node.Push(ASTNodeType.COMMAND, lexeme, _curLine);
        }

        private void ParseOperand(ASTNode node, string lexeme)
        {
            bool modifier = false;

            // An operand may start with an '@' symbol. Pick that
            // off.
            if (lexeme[0] == '@')
            {
                node.Push(ASTNodeType.QUIET, null, _curLine);
                lexeme = lexeme.Substring(1, lexeme.Length - 1);
                modifier = true;
            }

            // An operand may end with a '!' symbol. Pick that
            // off.
            if (lexeme.EndsWith("!"))
            {
                node.Push(ASTNodeType.FORCE, null, _curLine);
                lexeme = lexeme.Substring(0, lexeme.Length - 1);
                modifier = true;
            }

            if (!modifier)
                ParseValue(node, lexeme, ASTNodeType.OPERAND);
            else
                node.Push(ASTNodeType.OPERAND, lexeme, _curLine);
        }

        private void ParseOperator(ASTNode node, string lexeme)
        {
            switch (lexeme)
            {
                case "==":
                case "=":
                    node.Push(ASTNodeType.EQUAL, null, _curLine);
                    break;
                case "!=":
                    node.Push(ASTNodeType.NOT_EQUAL, null, _curLine);
                    break;
                case "<":
                    node.Push(ASTNodeType.LESS_THAN, null, _curLine);
                    break;
                case "<=":
                    node.Push(ASTNodeType.LESS_THAN_OR_EQUAL, null, _curLine);
                    break;
                case ">":
                    node.Push(ASTNodeType.GREATER_THAN, null, _curLine);
                    break;
                case ">=":
                    node.Push(ASTNodeType.GREATER_THAN_OR_EQUAL, null, _curLine);
                    break;
                default:
                    throw new RCESyntaxError(node, "Invalid operator in binary expression");
            }
        }

        private void ParseStatement(ASTNode node, string[] lexemes)
        {
            m_LastStatement = node.Push(ASTNodeType.STATEMENT, null, _curLine);

            // Examine the first word on the line
            switch (lexemes[0])
            {
                // Ignore comments
                case "#":
                case "//":
                    return;

                // Control flow statements are special
                case "if":
                    {
                        if (lexemes.Length <= 1)
                            throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                        var t = m_LastStatement.Push(ASTNodeType.IF, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "elseif":
                    {
                        if (lexemes.Length <= 1)
                            throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                        var t = m_LastStatement.Push(ASTNodeType.ELSEIF, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "else":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.ELSE, null, _curLine);
                    break;
                case "endif":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.ENDIF, null, _curLine);
                    break;
                case "while":
                    {
                        if (lexemes.Length <= 1)
                            throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                        var t = m_LastStatement.Push(ASTNodeType.WHILE, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "endwhile":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.ENDWHILE, null, _curLine);
                    break;
                case "for":
                    {
                        if (lexemes.Length <= 1)
                            throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                        ParseForLoop(m_LastStatement, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "endfor":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.ENDFOR, null, _curLine);
                    break;
                case "break":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.BREAK, null, _curLine);
                    break;
                case "continue":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.CONTINUE, null, _curLine);
                    break;
                case "stop":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.STOP, null, _curLine);
                    break;
                case "replay":
                case "loop":
                    if (lexemes.Length > 1)
                        throw new RCESyntaxError(m_LastStatement, "Script compilation error");

                    m_LastStatement.Push(ASTNodeType.REPLAY, null, _curLine);
                    break;
                default:
                    // It's a regular statement.
                    ParseCommand(m_LastStatement, lexemes[0]);

                    foreach (var lexeme in lexemes.Slice(1, lexemes.Length - 1))
                    {
                        ParseValue(m_LastStatement, lexeme, ASTNodeType.STRING);
                    }
                    break;
            }

        }

        private static bool IsOperator(string lexeme)
        {
            switch (lexeme)
            {
                case "==":
                case "=":
                case "!=":
                case "<":
                case "<=":
                case ">":
                case ">=":
                    return true;
            }

            return false;
        }

        private void ParseLogicalExpression(ASTNode node, string[] lexemes)
        {
            // The steam language supports logical operators 'and' and 'or'.
            // Catch those and split the expression into pieces first.
            // Fortunately, it does not support parenthesis.
            var expr = node;
            bool logical = false;
            int start = 0;

            for (int i = start; i < lexemes.Length; i++)
            {
                if (lexemes[i] == "and" || lexemes[i] == "or")
                {
                    if (!logical)
                    {
                        expr = node.Push(ASTNodeType.LOGICAL_EXPRESSION, null, _curLine);
                        logical = true;
                    }

                    ParseExpression(expr, lexemes.Slice(start, i - 1));
                    start = i + 1;
                    expr.Push(lexemes[i] == "and" ? ASTNodeType.AND : ASTNodeType.OR, null, _curLine);

                }
            }

            ParseExpression(expr, lexemes.Slice(start, lexemes.Length - 1));
        }

        private void ParseExpression(ASTNode node, string[] lexemes)
        {

            // The steam language supports both unary and
            // binary expressions. First determine what type
            // we have here.

            bool unary = false;
            bool binary = false;

            foreach (var lexeme in lexemes)
            {

                if (IsOperator(lexeme))
                {
                    // Operators mean it is a binary expression.
                    binary = true;
                }
            }

            // If no operators appeared, it's a unary expression
            if (!unary && !binary)
                unary = true;

            if (unary && binary)
                throw new RCESyntaxError(node, String.Format("Invalid expression at line {0}", node.LineNumber));

            if (unary)
                ParseUnaryExpression(node, lexemes);
            else
                ParseBinaryExpression(node, lexemes);
        }

        private void ParseUnaryExpression(ASTNode node, string[] lexemes)
        {
            var expr = node.Push(ASTNodeType.UNARY_EXPRESSION, null, _curLine);

            int i = 0;

            if (lexemes[i] == "not")
            {
                expr.Push(ASTNodeType.NOT, null, _curLine);
                i++;
            }

            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }
        }

        private void ParseBinaryExpression(ASTNode node, string[] lexemes)
        {
            var expr = node.Push(ASTNodeType.BINARY_EXPRESSION, null, _curLine);

            int i = 0;
            if (lexemes[i] == "not")
            {
                expr.Push(ASTNodeType.NOT, null, _curLine);
                i++;
            }

            // The expressions on either side of the operator can be values
            // or operands that need to be evaluated.
            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                if (IsOperator(lexemes[i]))
                    break;

                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }

            ParseOperator(expr, lexemes[i++]);

            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                if (IsOperator(lexemes[i]))
                    break;

                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }
        }

        private void ParseForLoop(ASTNode statement, string[] lexemes)
        {
            // There are 4 variants of for loops in steam. The simplest two just
            // iterate a fixed number of times. The other two iterate
            // parts of lists. We call those second two FOREACH.

            // We're intentionally deprecating one of the variants here.
            // The for X to Y in LIST variant may have some niche uses, but
            // is annoying to implement.

            // The for X loop remains supported as is, while the
            // The for X to Y variant, where both X and Y are integers,
            // is transformed to a for X.
            // for X in LIST form is unsupported and will probably crash


            // Reworking FOR implementation
            /* Dalamar
            for (end)                                ->    lexemes.Length == 1
            for (start) to (end)                     ->    lexemes.Length == 3
            for (start) to ('list name')             ->    lexemes.Length == 3 && startswith '
            for (start) to (end) in ('list name')    ->    lexemes.Length == 5
            */

            //Common Syntax check
            if (!matchNumber.IsMatch(lexemes[0]))
            {
                throw new RCESyntaxError(statement, "Invalid for loop: expected number got " + lexemes[0]);
            }

            if (lexemes.Length > 1 && lexemes[1] != "to" && lexemes[1] != "in")
            {
                throw new RCESyntaxError(statement, "Invalid for loop: missing 'to/in' keyword");
            }




            //CASE: for (end)
            if (lexemes.Length == 1)
            {
                if (!matchNumber.IsMatch(lexemes[0]))
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: expected number got " + lexemes[0]);
                }
                var loop = statement.Push(ASTNodeType.FOR, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
            }
            //CASE: for (start) to (end) in ('list name')
            else if (lexemes.Length == 5)
            {
                if (!matchNumber.IsMatch(lexemes[2]))
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: expected number got " + lexemes[2]);
                }
                if (lexemes[3] != "in" && lexemes[3] != "to")
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: missing 'in/to' keyword");
                }
                if (!matchListName.IsMatch(lexemes[4]))
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: list names must contain letters");
                }

                var loop = statement.Push(ASTNodeType.FOREACH, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
                ParseValue(loop, lexemes[2], ASTNodeType.STRING);
                ParseValue(loop, lexemes[4], ASTNodeType.LIST);
            }
            //CASE: for (start) to ('list name')
            else if (lexemes.Length == 3 && matchListName.IsMatch(lexemes[2]))
            {
                var loop = statement.Push(ASTNodeType.FOREACH, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
                ParseValue(loop, lexemes[2], ASTNodeType.LIST);
            }
            //CASE: for (start) to (end)
            else if (lexemes.Length == 3)
            {
                if (!matchNumber.IsMatch(lexemes[2]))
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: expected number got " + lexemes[2]);
                }
                int from = Int32.Parse(lexemes[0]);
                int to = Int32.Parse(lexemes[2]);
                int length = to - from;

                if (length < 0)
                {
                    throw new RCESyntaxError(statement, "Invalid for loop: loop count must be greater then 0, " + length.ToString() + " given ");
                }

                var loop = statement.Push(ASTNodeType.FOR, null, _curLine);
                ParseValue(loop, length.ToString(), ASTNodeType.STRING);
            }
            //CASE: syntax error
            else
            {
                throw new RCESyntaxError(statement, "Invalid for loop");
            }
        }

    }

    internal class TextParser
    {
        private readonly char[] _delimiters, _quotes;
        private readonly string[] _comments;
        private int _eol;
        private int _pos;
        private int _Size;
        private string _string;
        private bool _trim;

        public TextParser(string str, char[] delimiters, string[] comments, char[] quotes)
        {
            _delimiters = delimiters;
            _comments = comments;
            _quotes = quotes;
            _Size = str.Length;
            _string = str;
        }

        internal bool IsDelimiter()
        {
            bool result = false;

            for (int i = 0; i < _delimiters.Length && !result; i++)
                result = _string[_pos] == _delimiters[i];

            return result;
        }

        private void SkipToData()
        {
            while (_pos < _eol && IsDelimiter())
                _pos++;
        }

        private bool IsComment()
        {
            bool result = _string[_pos] == '\n';

            for (int i = 0; i < _comments.Length && !result; i++)
            {
                //Dalamar: added support for inline comments ( multi char )
                var comment = _comments[i];
                var char_left = _string.Length - _pos;

                result = _string.Substring(_pos, Math.Min(char_left, comment.Length)) == comment;

                /*
                if (result && i + 1 < _comments.Length && _comments[i] == _comments[i + 1] && _pos + 1 < _eol)
                {
                    result = _string[_pos] == _string[_pos + 1];
                    i++;
                }
                */
            }

            return result;
        }

        private string ObtainData()
        {
            StringBuilder result = new StringBuilder();

            while (_pos < _Size && _string[_pos] != '\n')
            {
                if (IsDelimiter())
                    break;

                if (IsComment())
                {
                    _pos = _eol;

                    break;
                }

                if (_string[_pos] != '\r' && (!_trim || _string[_pos] != ' ' && _string[_pos] != '\t'))
                    result.Append(_string[_pos]);

                _pos++;
            }

            return result.ToString();
        }

        private string ObtainQuotedData()
        {
            bool exit = false;
            string result = "";

            for (int i = 0; i < _quotes.Length; i += 2)
            {
                if (_string[_pos] == _quotes[i])
                {
                    char endQuote = _quotes[i + 1];
                    exit = true;

                    int pos = _pos + 1;
                    int start = pos;

                    while (pos < _eol && _string[pos] != '\n' && _string[pos] != endQuote)
                    {
                        if (_string[pos] == _quotes[i]) // another {
                        {
                            _pos = pos;
                            ObtainQuotedData(); // skip
                            pos = _pos;
                        }

                        pos++;
                    }

                    _pos++;
                    int size = pos - start;

                    if (size > 0)
                    {
                        result = _string.Substring(start, size).TrimEnd('\r', '\n');
                        _pos = pos;

                        if (_pos < _eol && _string[_pos] == endQuote)
                            _pos++;
                    }

                    break;
                }
            }

            if (!exit)
                result = ObtainData();

            return result;
        }

        internal string[] GetTokens(string str, bool trim = true)
        {
            _trim = trim;
            List<string> result = new List<string>();

            _pos = 0;
            _string = str;
            _Size = str.Length;
            _eol = _Size - 1;

            while (_pos < _eol)
            {
                SkipToData();

                if (IsComment())
                    break;

                string buf = ObtainQuotedData();

                if (buf.Length > 0)
                    result.Add(buf);
            }

            return result.ToArray();
        }
    }


}



#endregion
