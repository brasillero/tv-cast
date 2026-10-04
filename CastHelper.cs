using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

// Compilável com o compilador C# do .NET Framework presente no Windows 11.
internal static class CastHelper
{
    static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
    static readonly List<string> Messages = new List<string>();
    static readonly object ResultLock = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string RequestId;
    static bool Finished;

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] struct SourceInfo {
        public Luid Adapter; public uint Id, Mode, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct TargetInfo {
        public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling;
        public Rational Refresh; public uint Scanline; public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct PathInfo {
        public SourceInfo Source; public TargetInfo Target; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct DeviceHeader {
        public uint Type, Size; public Luid Adapter; public uint Id;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TargetName {
        public DeviceHeader Header; public uint Flags, Technology;
        public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, uint size, out uint needed);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    sealed class Displays {
        public bool Known, Connected;
        public int Error;
        public List<string> Names = new List<string>();
    }

    static Displays Probe()
    {
        Displays result = new Displays();
        // PathInfo tem 72 bytes; DISPLAYCONFIG_MODE_INFO tem 64 bytes.
        for (int retry = 0; retry < 3; retry++) {
            uint paths, modes;
            int error = GetDisplayConfigBufferSizes(2, out paths, out modes);
            if (error != 0) { result.Error = error; return result; }
            // PC sem monitor físico: nenhum caminho ativo é um resultado válido.
            // QueryDisplayConfig rejeita buffers de tamanho zero com erro 87.
            if (paths == 0) { result.Known = true; return result; }
            PathInfo[] items = new PathInfo[Math.Max(paths, 1)];
            IntPtr modeBuffer = Marshal.AllocHGlobal(checked((int)Math.Max(modes, 1) * 64));
            try {
                error = QueryDisplayConfig(2, ref paths, items, ref modes, modeBuffer, IntPtr.Zero);
                if (error == 122) continue; // Mudança de topologia durante a consulta.
                if (error != 0) { result.Error = error; return result; }
                result.Known = true;
                for (int i = 0; i < paths; i++) {
                    TargetName name = new TargetName();
                    name.Header.Type = 2;
                    name.Header.Size = (uint)Marshal.SizeOf(typeof(TargetName));
                    name.Header.Adapter = items[i].Target.Adapter;
                    name.Header.Id = items[i].Target.Id;
                    int nameError = DisplayConfigGetDeviceInfo(ref name);
                    // QueryDisplayConfig pode descrever a saída do receptor como HDMI;
                    // GetDeviceInfo descreve a tecnologia do destino Miracast.
                    if (items[i].Target.Technology == 15 || (nameError == 0 && name.Technology == 15)) {
                        result.Connected = true;
                        result.Names.Add(nameError == 0 && !String.IsNullOrWhiteSpace(name.FriendlyName)
                            ? name.FriendlyName : "monitor sem fio");
                    }
                }
                return result;
            } finally { Marshal.FreeHGlobal(modeBuffer); }
        }
        result.Error = 122;
        return result;
    }

    static bool Unlocked()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x101);
        if (desktop == IntPtr.Zero) return false;
        try {
            StringBuilder name = new StringBuilder(256);
            uint needed;
            return GetUserObjectInformation(desktop, 2, name, 512, out needed) &&
                String.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        } finally { CloseDesktop(desktop); }
    }

    static bool Contains(string text, string part)
    {
        return (text ?? "").IndexOf(part ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string Normal(string text)
    {
        StringBuilder s = new StringBuilder();
        foreach (char c in (text ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) s.Append(Char.ToLowerInvariant(c));
        return s.ToString();
    }

    static bool DisconnectName(string name)
    {
        string n = Normal(name).Trim();
        return n == "disconnect" || n.StartsWith("disconnect ") || n == "desconectar" || n.StartsWith("desconectar ");
    }

    static List<AutomationElement> Panels()
    {
        List<AutomationElement> result = new List<AutomationElement>();
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows) {
            try {
                string process = Process.GetProcessById(window.Current.ProcessId).ProcessName;
                string name = Normal(window.Current.Name);
                string cls = window.Current.ClassName;
                bool shell = String.Equals(process, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(process, "ShellHost", StringComparison.OrdinalIgnoreCase);
                bool title = name == "cast" || name == "transmitir" || name == "transmissao" || name == "connect" || name == "conectar";
                if (window.Current.IsOffscreen || !shell ||
                    (!title && cls != "ControlCenterWindow" && cls != "Windows.UI.Core.CoreWindow")) continue;
                if (!title) {
                    // O mesmo host também contém notificações e configurações rápidas.
                    Condition castTitle = new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Cast"),
                        new PropertyCondition(AutomationElement.NameProperty, "Transmitir"),
                        new PropertyCondition(AutomationElement.NameProperty, "Transmissão"),
                        new PropertyCondition(AutomationElement.NameProperty, "Conectar"),
                        new PropertyCondition(AutomationElement.NameProperty, "Connect"));
                    if (window.FindFirst(TreeScope.Descendants, castTitle) == null) continue;
                }
                result.Add(window);
            } catch (ElementNotAvailableException) { }
              catch (ArgumentException) { }
              catch (InvalidOperationException) { }
        }
        return result;
    }

    static AutomationElement FindDisconnect()
    {
        foreach (AutomationElement panel in Panels()) {
            AutomationElementCollection buttons = panel.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons) {
                try {
                    if (button.Current.IsEnabled && !button.Current.IsOffscreen && DisconnectName(button.Current.Name)) return button;
                } catch (ElementNotAvailableException) { }
            }
        }
        return null;
    }

    static AutomationElement FindDevice(string device)
    {
        List<AutomationElement> exact = new List<AutomationElement>();
        List<AutomationElement> partial = new List<AutomationElement>();
        foreach (AutomationElement panel in Panels()) {
            AutomationElementCollection elements = panel.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement item in elements) {
                try {
                    ControlType type = item.Current.ControlType;
                    if (item.Current.IsOffscreen || !item.Current.IsEnabled ||
                        (type != ControlType.ListItem && type != ControlType.Button && type != ControlType.Text)) continue;
                    string name = item.Current.Name;
                    if (String.Equals(name, device, StringComparison.OrdinalIgnoreCase)) exact.Add(item);
                    else if (Contains(name, device)) partial.Add(item);
                } catch (ElementNotAvailableException) { }
            }
        }
        List<AutomationElement> candidates = exact.Count > 0 ? exact : partial;
        // Correspondência parcial com mais de um nome distinto é ambígua.
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AutomationElement item in candidates) names.Add(item.Current.Name);
        if (names.Count > 1) throw new InvalidOperationException("Nome ambíguo no painel Cast; informe o nome completo do dispositivo.");
        foreach (AutomationElement item in candidates)
            if (item.Current.ControlType == ControlType.ListItem || item.Current.ControlType == ControlType.Button) return item;
        foreach (AutomationElement item in candidates) {
            AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(item);
            for (int depth = 0; parent != null && depth < 3; depth++, parent = TreeWalker.ControlViewWalker.GetParent(parent)) {
                ControlType type = parent.Current.ControlType;
                if ((type == ControlType.ListItem || type == ControlType.Button) && Contains(parent.Current.Name, device)) return parent;
            }
            return item; // Último recurso: retângulo do texto encontrado pelo nome.
        }
        return null;
    }

    static void Activate(AutomationElement item)
    {
        if (!Unlocked()) throw new InvalidOperationException("Windows bloqueado; desbloqueie antes de usar o painel Cast.");
        object pattern;
        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) {
            try { ((InvokePattern)pattern).Invoke(); Messages.Add("Controle ativado por UI Automation (InvokePattern)."); return; }
            catch (InvalidOperationException) { }
        }
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) {
            try { ((SelectionItemPattern)pattern).Select(); Messages.Add("Controle selecionado por UI Automation (SelectionItemPattern)."); return; }
            catch (InvalidOperationException) { }
        }
        System.Windows.Rect rect = item.Current.BoundingRectangle;
        if (item.Current.IsOffscreen || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("O controle encontrado não tem área visível para clicar.");
        int x = (int)(rect.X + rect.Width / 2), y = (int)(rect.Y + rect.Height / 2);
        if (!SystemInformation.VirtualScreen.Contains(x, y))
            throw new InvalidOperationException("O controle está fora da área dos monitores atuais.");
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Não foi possível posicionar o cursor na sessão interativa.");
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Messages.Add("Clique no centro do controle localizado pelo nome; nenhuma coordenada fixa foi usada.");
    }

    static void OpenCast()
    {
        if (!Unlocked()) throw new InvalidOperationException("Windows bloqueado; desbloqueie antes de usar Win+K.");
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        try {
            Thread.Sleep(50);
            keybd_event(0x4B, 0, 0, UIntPtr.Zero);
            // Sem esta pausa o Windows costuma ignorar o Win+K sintético.
            Thread.Sleep(100);
        }
        finally {
            keybd_event(0x4B, 0, 2, UIntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(0x5B, 0, 2, UIntPtr.Zero);
        }
        Thread.Sleep(2500);
    }

    static int Operate(Dictionary<string, object> request)
    {
        string action = (string)request["action"];
        bool dry = (bool)request["dryRun"];
        Displays before = Probe();
        if (action == "connect" && before.Connected) {
            Messages.Add("Projeção Miracast já ativa: " + String.Join(", ", before.Names.ToArray()) + ". Nenhuma ação realizada.");
            Messages.Add("O Windows pode usar o nome do monitor, diferente do nome da TV. A conexão existente foi preservada.");
            return 0;
        }
        if (dry) {
            Messages.Add("Simulação de " + action + ": nenhuma tecla, clique ou alteração da projeção.");
            Messages.Add("Sessão interativa: " + Process.GetCurrentProcess().SessionId + "; desktop desbloqueado: " + (Unlocked() ? "sim" : "não") + ".");
            if (action == "connect") Messages.Add("Dispositivo solicitado: " + (string)request["device"] + ".");
            if (action == "mode") Messages.Add("Modo solicitado: " + (string)request["mode"] + ".");
            return 0;
        }
        if (!before.Known) {
            Messages.Add("Estado da projeção inconclusivo (QueryDisplayConfig: " + before.Error + "). Nenhuma ação realizada.");
            return 4;
        }
        if (!Unlocked()) { Messages.Add("Windows bloqueado; desbloqueie para controlar a projeção."); return 4; }
        if (action == "mode") {
            if (!before.Connected) { Messages.Add("Nenhuma projeção Miracast confirmada para alterar o modo."); return 4; }
            string mode = (string)request["mode"];
            if (mode != "clone" && mode != "extend") throw new ArgumentException("Modo inválido.");
            ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe"), mode == "clone" ? "/clone" : "/extend");
            info.UseShellExecute = false;
            using (Process p = Process.Start(info)) {
                if (!p.WaitForExit(10000)) { p.Kill(); Messages.Add("Timeout ao executar DisplaySwitch."); return 124; }
                if (p.ExitCode != 0) { Messages.Add("DisplaySwitch terminou com código " + p.ExitCode + "."); return 3; }
            }
            Messages.Add("Solicitado modo " + mode + " na sessão interativa; confirme o resultado na TV.");
            return 0;
        }
        if (action != "connect" && action != "disconnect") throw new ArgumentException("Ação inválida.");
        AutomationElement disconnect = FindDisconnect();
        if (action == "connect" && disconnect != null) {
            Messages.Add("O painel Cast já mostra uma conexão com botão Desconectar. Nenhuma ação realizada.");
            return 0;
        }
        if (disconnect == null) OpenCast();
        Stopwatch wait = Stopwatch.StartNew();
        AutomationElement target = null;
        bool retried = disconnect != null;
        while (wait.Elapsed.TotalSeconds < 15) {
            if (!retried && wait.Elapsed.TotalSeconds > 4 && Panels().Count == 0) {
                Messages.Add("Painel Cast não abriu; repetindo Win+K.");
                OpenCast();
                retried = true;
            }
            disconnect = FindDisconnect();
            if (action == "connect" && disconnect != null) {
                Messages.Add("O painel Cast já mostra uma conexão ativa. Nenhuma seleção realizada."); return 0;
            }
            target = action == "disconnect" ? disconnect : FindDevice((string)request["device"]);
            if (target != null) break;
            Thread.Sleep(600);
        }
        if (target == null) {
            Messages.Add(action == "disconnect" ? "Botão Desconectar não encontrado no painel Cast; nenhuma alteração realizada."
                : "Dispositivo não encontrado no painel Cast: " + (string)request["device"] + ". Ative o espelhamento na TV e use o nome completo.");
            return 5;
        }
        Activate(target);
        wait.Restart();
        while (wait.Elapsed.TotalSeconds < 26) {
            Thread.Sleep(900);
            Displays current = Probe();
            AutomationElement button = FindDisconnect();
            if (action == "connect" && (current.Connected || button != null)) {
                Messages.Add("Conexão Miracast confirmada após selecionar: " + (string)request["device"] + "."); return 0;
            }
            if (action == "disconnect" && current.Known && !current.Connected && button == null) {
                Messages.Add("Projeção encerrada."); return 0;
            }
        }
        Messages.Add("Timeout ao confirmar " + action + ". Consulte tv-cast status; a TV pode pedir autorização na primeira conexão.");
        return 124;
    }

    static void WriteResult(int code, bool connected)
    {
        lock (ResultLock) {
            if (Finished) return;
            Finished = true;
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["requestId"] = RequestId;
            result["code"] = code;
            result["connected"] = connected;
            result["messages"] = Messages.ToArray();
            string path = Path.Combine(Base, "helper-result.json");
            string part = Path.Combine(Base, "helper-result.partial.json");
            File.WriteAllText(part, Json.Serialize(result), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }
    }

    [STAThread] static int Main(string[] args)
    {
        int code = 3;
        bool connected = false;
        System.Threading.Timer watchdog = null;
        try {
            SetProcessDPIAware();
            Dictionary<string, object> request = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Base, "request.json"), Encoding.UTF8));
            RequestId = (string)request["requestId"];
            watchdog = new System.Threading.Timer(delegate {
                lock (ResultLock) {
                    if (!Finished) { Messages.Add("Timeout de 50 s no auxiliar Windows."); WriteResult(124, false); }
                }
                Environment.Exit(124);
            }, null, 50000, Timeout.Infinite);
            if (args.Length == 1 && args[0] == "probe") {
                Displays state = Probe();
                connected = state.Connected;
                code = state.Known ? 0 : 4;
                Messages.Add(!state.Known ? "Estado Miracast: inconclusivo (erro Windows " + state.Error + ")."
                    : state.Connected ? "Estado Miracast: conectado (" + String.Join(", ", state.Names.ToArray()) + ")."
                    : "Estado Miracast: nenhuma saída sem fio ativa identificada.");
            } else if (args.Length == 1 && args[0] == "ui") code = Operate(request);
            else throw new ArgumentException("Use probe ou ui.");
        } catch (Exception exc) { Messages.Add("Erro: " + exc.Message); code = 3; }
        finally {
            if (watchdog != null) watchdog.Dispose();
            WriteResult(code, connected);
        }
        return code;
    }
}
