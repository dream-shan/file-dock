using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace FileDockDemo.Features.ControlCenter;

internal enum WifiSecurity { Open, Wpa2Personal, Wpa3Personal, Other }

internal sealed record WifiNetwork(string HexSsid,string Name,string? Profile,WifiSecurity Security,uint Signal,bool Connected,bool Connectable,uint Authentication,uint Cipher)
{
    internal bool CanJoin=>Connectable&&(Profile!=null||Security!=WifiSecurity.Other);
    internal string Description => (Security switch
    {
        WifiSecurity.Open=>"开放网络", WifiSecurity.Wpa2Personal=>"WPA2", WifiSecurity.Wpa3Personal=>"WPA3", _=>"受保护"
    }) + $" · {Signal}%" + (Connected?" · 当前连接":"") + (Profile!=null?" · 已保存":"");
    internal bool SameTarget(WifiNetwork other)=>HexSsid==other.HexSsid&&Authentication==other.Authentication&&Cipher==other.Cipher&&Profile==other.Profile;
}

internal sealed class WirelessException(string message):Exception(message);

internal static class WifiProfile
{
    private static readonly XNamespace ProfileNamespace="http://www.microsoft.com/networking/WLAN/profile/v1";
    internal static string SsidName(byte[] ssid)
    {
        try
        {
            string name=new UTF8Encoding(false,true).GetString(ssid);
            return string.IsNullOrWhiteSpace(name)||name.Any(char.IsControl) ? Convert.ToHexString(ssid) : name;
        }
        catch(DecoderFallbackException){return Convert.ToHexString(ssid);}
    }

    internal static string Create(WifiNetwork network,string profileName,string password,bool automatic)
    {
        var security=network.Security;
        if(security==WifiSecurity.Other)throw new WirelessException("请在 Windows 设置中连接这种网络。");
        if(network.HexSsid.Length is <2 or >64 || network.HexSsid.Length%2!=0 || !network.HexSsid.All(Uri.IsHexDigit))
            throw new WirelessException("网络标识无效，请刷新列表。");
        bool hexKey=security==WifiSecurity.Wpa2Personal && password.Length==64 && password.All(Uri.IsHexDigit);
        if(security!=WifiSecurity.Open&&!hexKey&&(password.Length is <8 or >63||password.Any(c=>c<' '||c>'~')))
            throw new WirelessException("请输入 8 至 63 位 ASCII 密码，WPA2 也支持 64 位十六进制密钥。");
        string authentication=security switch{WifiSecurity.Wpa2Personal=>"WPA2PSK",WifiSecurity.Wpa3Personal=>"WPA3SAE",_=>"open"};
        XElement Element(string name,params object[] content)=>new(ProfileNamespace+name,content);
        var protection=Element("security",Element("authEncryption",Element("authentication",authentication),Element("encryption",security==WifiSecurity.Open?"none":"AES"),Element("useOneX","false")));
        if(security!=WifiSecurity.Open)
            protection.Add(Element("sharedKey",Element("keyType",hexKey?"networkKey":"passPhrase"),Element("protected","false"),Element("keyMaterial",password)));
        return Element("WLANProfile",Element("name",profileName),Element("SSIDConfig",Element("SSID",Element("hex",network.HexSsid))),
            Element("connectionType","ESS"),Element("connectionMode",automatic?"auto":"manual"),Element("MSM",protection)).ToString(SaveOptions.DisableFormatting);
    }
}
