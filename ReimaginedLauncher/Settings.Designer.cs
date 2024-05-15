namespace ReimaginedLauncher.Settings;

[global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
[global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.VisualStudio.Editors.SettingsDesigner.SettingsSingleFileGenerator", "17.0.0.0")]
internal sealed partial class Properties : global::System.Configuration.ApplicationSettingsBase {
        
    private static Properties defaultInstance = ((Properties)(global::System.Configuration.ApplicationSettingsBase.Synchronized(new Properties())));
        
    public static Properties Default {
        get {
            return defaultInstance;
        }
    }
        
    [global::System.Configuration.UserScopedSettingAttribute()]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Configuration.DefaultSettingValueAttribute("")]
    public string D2RExePath {
        get {
            return ((string)(this["D2RExePath"]));
        }
        set {
            this["D2RExePath"] = value;
        }
    }
}