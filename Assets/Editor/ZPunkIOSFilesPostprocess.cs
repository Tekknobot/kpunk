#if UNITY_EDITOR
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

// Makes Documents/ZPunk accessible in Files > On My iPhone > ZPunk.
// Avoid UnityEditor.iOS.Xcode assembly dependency on non-iOS build hosts.
public static class ZPunkIOSFilesPostprocess
{
    [PostProcessBuild(110)]
    private static void EnableDocumentsFolder(BuildTarget target, string buildPath)
    {
        if (target != BuildTarget.iOS) return;
        string plist = Path.Combine(buildPath, "Info.plist");
        if (!File.Exists(plist)) return;
        var xml = new XmlDocument();
        xml.XmlResolver = null;
        xml.Load(plist);
        XmlNode dict = xml.SelectSingleNode("/plist/dict");
        if (dict == null) return;
        SetBool(dict, xml, "UIFileSharingEnabled", true);
        SetBool(dict, xml, "LSSupportsOpeningDocumentsInPlace", true);
        xml.Save(plist);
        Debug.Log("[ZPunk] Enabled Files document sharing for Documents/ZPunk.");
    }
    private static void SetBool(XmlNode dict, XmlDocument xml, string name, bool enabled)
    {
        XmlNode current = dict.FirstChild;
        while (current != null)
        {
            XmlNode next = current.NextSibling;
            if (current.Name == "key" && current.InnerText == name)
            {
                XmlNode value = current.NextSibling;
                if (value != null) dict.RemoveChild(value);
                dict.RemoveChild(current);
                break;
            }
            current = next;
        }
        XmlElement key = xml.CreateElement("key");
        key.InnerText = name;
        dict.AppendChild(key);
        dict.AppendChild(xml.CreateElement(enabled ? "true" : "false"));
    }
}
#endif
