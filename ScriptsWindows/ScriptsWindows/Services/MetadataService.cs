using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScriptsWindows.Services;

/// <summary>Une information cachée trouvée dans un fichier. Sensitive = à retirer avant de partager (position, identité, appareil).</summary>
public sealed record MetaItem(string Label, string Value, bool Sensitive);

public sealed class MetaReport
{
    public string Path { get; init; } = "";
    public string Kind { get; set; } = "";
    public List<MetaItem> Items { get; } = new();
    public (double Lat, double Lon)? Gps { get; set; }
    public bool CanClean { get; set; }
    public string Error { get; set; } = "";
    public int SensitiveCount => Items.Count(i => i.Sensitive) + (Gps != null ? 1 : 0);
    /// <summary>Le nettoyage automatique retirerait quelque chose (les commentaires et modifications suivies d'Office restent à traiter dans Word).</summary>
    public bool NeedsClean => CanClean && (Gps != null || Items.Any(i => i.Sensitive && !i.Label.StartsWith("Commentaires dans") && !i.Label.StartsWith("Modifications suivies")));
    public string MapUrl => Gps is { } g ? $"https://www.openstreetmap.org/?mlat={g.Lat.ToString("0.000000", CultureInfo.InvariantCulture)}&mlon={g.Lon.ToString("0.000000", CultureInfo.InvariantCulture)}#map=16/{g.Lat.ToString("0.0000", CultureInfo.InvariantCulture)}/{g.Lon.ToString("0.0000", CultureInfo.InvariantCulture)}" : "";
}

/// <summary>
/// Métadonnées cachées des photos et documents (sans outil externe) : EXIF (JPEG, TIFF, HEIC, WebP, PNG eXIf), XMP,
/// texte PNG, propriétés Office (docProps) + commentaires et modifications suivies, informations PDF.
/// Nettoyage : toujours une COPIE « (sans métadonnées) » ; l'original n'est jamais modifié. JPEG : segments EXIF / XMP /
/// IPTC / commentaire retirés, orientation conservée ; PNG : blocs texte / EXIF / date retirés ; Office : propriétés vidées.
/// </summary>
public static class MetadataService
{
    public static readonly string[] ImageExt = { ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".png", ".heic", ".heif", ".webp", ".dng" };
    public static readonly string[] OfficeExt = { ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm" };
    public static bool Supported(string path) { string e = System.IO.Path.GetExtension(path).ToLowerInvariant(); return ImageExt.Contains(e) || OfficeExt.Contains(e) || e == ".pdf"; }

    public static MetaReport Inspect(string path)
    {
        var r = new MetaReport { Path = path };
        try
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (OfficeExt.Contains(ext)) { r.Kind = "Document Office"; InspectOffice(path, r); r.CanClean = true; }
            else if (ext == ".pdf") { r.Kind = "PDF"; InspectPdf(path, r); }
            else
            {
                r.Kind = "Image";
                byte[] data = ReadHead(path, 4 * 1024 * 1024);
                if (ext == ".png") InspectPng(data, r);
                else { int t = FindTiff(data); if (t >= 0) ParseExif(data, t, r); }
                InspectXmp(data, r);
                r.CanClean = ext is ".jpg" or ".jpeg" or ".jfif" or ".png";
            }
        }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }

    private static byte[] ReadHead(string path, int max)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(max, fs.Length)];
        int n = 0, k;
        while (n < buf.Length && (k = fs.Read(buf, n, buf.Length - n)) > 0) n += k;
        return buf;
    }

    // ─── EXIF ───
    private static int IndexOf(byte[] d, byte[] pat, int start = 0)
    {
        for (int i = start; i <= d.Length - pat.Length; i++)
        {
            int j = 0; while (j < pat.Length && d[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }

    /// <summary>Début de l'en-tête TIFF des données EXIF (après « Exif\0\0 », ou TIFF nu dans HEIC / WebP / TIFF).</summary>
    internal static int FindTiff(byte[] d)
    {
        if (d.Length > 4 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42))) return 0;
        int e = IndexOf(d, new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 });
        if (e >= 0 && e + 10 < d.Length) return e + 6;
        int a = IndexOf(d, new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 });
        int b = IndexOf(d, new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 });
        return a >= 0 && (b < 0 || a < b) ? a : b;
    }

    private sealed class Tiff
    {
        public byte[] D = null!; public int Base; public bool Le;
        public int U16(int o) => o + 2 > D.Length ? 0 : Le ? D[o] | D[o + 1] << 8 : D[o] << 8 | D[o + 1];
        public uint U32(int o) => o + 4 > D.Length ? 0 : Le ? (uint)(D[o] | D[o + 1] << 8 | D[o + 2] << 16 | D[o + 3] << 24) : (uint)(D[o] << 24 | D[o + 1] << 16 | D[o + 2] << 8 | D[o + 3]);
    }

    private sealed record Entry(int Tag, int Type, int Count, int ValueOffset);

    private static List<Entry> ReadIfd(Tiff t, uint offset)
    {
        var list = new List<Entry>();
        int p = t.Base + (int)offset;
        if (offset == 0 || p + 2 > t.D.Length) return list;
        int n = Math.Min(t.U16(p), 500);
        for (int i = 0; i < n; i++)
        {
            int e = p + 2 + i * 12;
            if (e + 12 > t.D.Length) break;
            int type = t.U16(e + 2), count = (int)Math.Min(t.U32(e + 4), 1_000_000);
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 1 } * count;
            int vo = size <= 4 ? e + 8 : t.Base + (int)t.U32(e + 8);
            list.Add(new Entry(t.U16(e), type, count, vo));
        }
        return list;
    }

    private static string Ascii(Tiff t, Entry e)
    {
        if (e.ValueOffset < 0 || e.ValueOffset >= t.D.Length) return "";
        int len = Math.Min(e.Count, t.D.Length - e.ValueOffset);
        return Encoding.UTF8.GetString(t.D, e.ValueOffset, len).TrimEnd('\0', ' ').Trim();
    }

    private static string Ucs2(Tiff t, Entry e)   // champs XP* de Windows (UTF-16 LE)
    {
        if (e.ValueOffset < 0 || e.ValueOffset >= t.D.Length) return "";
        return Encoding.Unicode.GetString(t.D, e.ValueOffset, Math.Min(e.Count, t.D.Length - e.ValueOffset) & ~1).TrimEnd('\0').Trim();
    }

    private static double Rational(Tiff t, int o) { uint n = t.U32(o), d = t.U32(o + 4); return d == 0 ? 0 : (double)n / d; }

    internal static void ParseExif(byte[] d, int tiffStart, MetaReport r)
    {
        if (tiffStart < 0 || tiffStart + 8 > d.Length) return;
        var t = new Tiff { D = d, Base = tiffStart, Le = d[tiffStart] == 'I' };
        if (t.U16(tiffStart + 2) != 42) return;
        var ifd0 = ReadIfd(t, t.U32(tiffStart + 4));
        void Add(string label, string v, bool sens) { if (!string.IsNullOrWhiteSpace(v) && !r.Items.Any(i => i.Label == label)) r.Items.Add(new MetaItem(label, v, sens)); }
        uint exifPtr = 0, gpsPtr = 0;
        foreach (var e in ifd0)
            switch (e.Tag)
            {
                case 0x010F: Add("Marque de l'appareil", Ascii(t, e), true); break;
                case 0x0110: Add("Modèle de l'appareil", Ascii(t, e), true); break;
                case 0x0131: Add("Logiciel", Ascii(t, e), false); break;
                case 0x0132: Add("Date de modification", Ascii(t, e), false); break;
                case 0x013B: Add("Auteur", Ascii(t, e), true); break;
                case 0x8298: Add("Copyright", Ascii(t, e), true); break;
                case 0x010E: Add("Description", Ascii(t, e), true); break;
                case 0x9C9D: Add("Auteur (Windows)", Ucs2(t, e), true); break;
                case 0x9C9B: Add("Titre (Windows)", Ucs2(t, e), false); break;
                case 0x9C9C: Add("Commentaire (Windows)", Ucs2(t, e), true); break;
                case 0x0112: int o = t.U16(e.ValueOffset); if (o > 1) Add("Orientation", o.ToString(), false); break;
                case 0x8769: exifPtr = t.U32(e.ValueOffset); break;
                case 0x8825: gpsPtr = t.U32(e.ValueOffset); break;
            }
        foreach (var e in ReadIfd(t, exifPtr))
            switch (e.Tag)
            {
                case 0x9003: Add("Date de prise de vue", Ascii(t, e), false); break;
                case 0xA430: Add("Propriétaire de l'appareil", Ascii(t, e), true); break;
                case 0xA431: Add("N° de série de l'appareil", Ascii(t, e), true); break;
                case 0xA433: Add("Marque de l'objectif", Ascii(t, e), false); break;
                case 0xA434: Add("Objectif", Ascii(t, e), false); break;
                case 0xA420: Add("Identifiant unique de l'image", Ascii(t, e), true); break;
                case 0x9286:
                    if (e.ValueOffset + 8 < d.Length && e.Count > 8) { var c = Ascii(t, e with { ValueOffset = e.ValueOffset + 8, Count = e.Count - 8 }); if (c.Length > 0 && c.Any(char.IsLetterOrDigit)) Add("Commentaire", c, true); }
                    break;
            }
        var gps = ReadIfd(t, gpsPtr);
        if (gps.Count > 0)
        {
            string latRef = "", lonRef = ""; double? lat = null, lon = null, alt = null;
            foreach (var e in gps)
                switch (e.Tag)
                {
                    case 1: latRef = Ascii(t, e); break;
                    case 3: lonRef = Ascii(t, e); break;
                    case 2 when e.Count >= 3: lat = Rational(t, e.ValueOffset) + Rational(t, e.ValueOffset + 8) / 60 + Rational(t, e.ValueOffset + 16) / 3600; break;
                    case 4 when e.Count >= 3: lon = Rational(t, e.ValueOffset) + Rational(t, e.ValueOffset + 8) / 60 + Rational(t, e.ValueOffset + 16) / 3600; break;
                    case 6: alt = Rational(t, e.ValueOffset); break;
                }
            if (lat is > 0 or < 0 && lon is > 0 or < 0 && Math.Abs(lat.Value) <= 90 && Math.Abs(lon.Value) <= 180)
            {
                double la = latRef.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? -lat.Value : lat.Value;
                double lo = lonRef.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? -lon.Value : lon.Value;
                r.Gps = (la, lo);
                r.Items.Add(new MetaItem("Position GPS", $"{la.ToString("0.00000", CultureInfo.InvariantCulture)}, {lo.ToString("0.00000", CultureInfo.InvariantCulture)}" +
                                                      (alt is > 0 ? $" (altitude {alt:0} m)" : ""), true));
            }
        }
    }

    // ─── XMP (photos retouchées, PDF) ───
    private static readonly (string Pattern, string Label, bool Sensitive)[] XmpFields =
    {
        (@"<dc:creator>.*?<rdf:li[^>]*>(.*?)</rdf:li>", "Auteur (XMP)", true), (@"xmp:CreatorTool=""([^""]+)""|<xmp:CreatorTool>(.*?)</xmp:CreatorTool>", "Logiciel (XMP)", false),
        (@"photoshop:City=""([^""]+)""|<photoshop:City>(.*?)</photoshop:City>", "Ville", true), (@"photoshop:Country=""([^""]+)""|<photoshop:Country>(.*?)</photoshop:Country>", "Pays", true),
        (@"exif:GPSLatitude=""([^""]+)""|<exif:GPSLatitude>(.*?)</exif:GPSLatitude>", "Latitude (XMP)", true), (@"exif:GPSLongitude=""([^""]+)""|<exif:GPSLongitude>(.*?)</exif:GPSLongitude>", "Longitude (XMP)", true),
        (@"pdf:Producer=""([^""]+)""|<pdf:Producer>(.*?)</pdf:Producer>", "Producteur PDF (XMP)", false),
    };

    internal static void InspectXmp(byte[] d, MetaReport r)
    {
        int s = IndexOf(d, Encoding.ASCII.GetBytes("<x:xmpmeta"));
        if (s < 0) return;
        int e = IndexOf(d, Encoding.ASCII.GetBytes("</x:xmpmeta>"), s);
        string xmp = Encoding.UTF8.GetString(d, s, (e > s ? e + 12 : Math.Min(d.Length, s + 200_000)) - s);
        foreach (var (pat, label, sens) in XmpFields)
        {
            var m = Regex.Match(xmp, pat, RegexOptions.Singleline);
            string v = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "";
            v = System.Net.WebUtility.HtmlDecode(Regex.Replace(v, "<[^>]+>", "")).Trim();
            if (v.Length > 0 && !r.Items.Any(i => i.Label == label)) r.Items.Add(new MetaItem(label, v.Length > 200 ? v[..200] + "…" : v, sens));
        }
    }

    // ─── PNG ───
    internal static void InspectPng(byte[] d, MetaReport r)
    {
        foreach (var (type, start, len) in PngChunks(d))
        {
            if (type == "eXIf") { var exif = new byte[len]; Array.Copy(d, start, exif, 0, len); ParseExif(exif, 0, r); }
            else if (type is "tEXt" or "iTXt")
            {
                int z = Array.IndexOf(d, (byte)0, start, len);
                if (z < 0) continue;
                string key = Encoding.Latin1.GetString(d, start, z - start);
                string val;
                if (type == "tEXt") val = Encoding.Latin1.GetString(d, z + 1, start + len - z - 1);
                else
                {   // iTXt : clé \0 compression méthode langue \0 clé traduite \0 texte (non compressé seulement)
                    if (d[z + 1] != 0) continue;
                    int p = z + 3; for (int k = 0; k < 2 && p < start + len; k++) { int q = Array.IndexOf(d, (byte)0, p, start + len - p); if (q < 0) { p = start + len; break; } p = q + 1; }
                    val = p < start + len ? Encoding.UTF8.GetString(d, p, start + len - p) : "";
                }
                if (key == "XML:com.adobe.xmp") { InspectXmp(Encoding.UTF8.GetBytes(val), r); continue; }
                bool sens = key is "Author" or "Copyright" or "Comment" or "Description" or "Source" or "Disclaimer";
                if (val.Trim().Length > 0) r.Items.Add(new MetaItem($"Texte PNG « {key} »", val.Length > 200 ? val[..200] + "…" : val.Trim(), sens));
            }
            else if (type == "tIME") r.Items.Add(new MetaItem("Date de modification (PNG)", $"{(d[start] << 8) | d[start + 1]}-{d[start + 2]:00}-{d[start + 3]:00}", false));
        }
    }

    private static IEnumerable<(string Type, int Start, int Len)> PngChunks(byte[] d)
    {
        int p = 8;
        while (p + 12 <= d.Length)
        {
            int len = d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3];
            if (len < 0 || p + 12 + len > d.Length) yield break;
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            yield return (type, p + 8, len);
            if (type == "IEND") yield break;
            p += 12 + len;
        }
    }

    // ─── Office ───
    private static readonly Dictionary<string, (string Label, bool Sensitive)> OfficeFields = new()
    {
        ["creator"] = ("Auteur", true), ["lastModifiedBy"] = ("Dernière modification par", true), ["title"] = ("Titre", false), ["subject"] = ("Sujet", false),
        ["keywords"] = ("Mots-clés", false), ["description"] = ("Commentaires du document", true), ["category"] = ("Catégorie", false),
        ["created"] = ("Créé le", false), ["modified"] = ("Modifié le", false), ["lastPrinted"] = ("Imprimé le", false), ["revision"] = ("Révision", false),
        ["Company"] = ("Société", true), ["Manager"] = ("Responsable", true), ["Application"] = ("Logiciel", false), ["Template"] = ("Modèle", false), ["TotalTime"] = ("Temps d'édition (min)", false),
    };

    internal static void InspectOffice(string path, MetaReport r)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var part in new[] { "docProps/core.xml", "docProps/app.xml" })
        {
            var e = zip.GetEntry(part); if (e == null) continue;
            using var s = e.Open();
            foreach (var el in XDocument.Load(s).Root?.Elements() ?? Enumerable.Empty<XElement>())
                if (OfficeFields.TryGetValue(el.Name.LocalName, out var f) && el.Value.Trim().Length > 0 && !(el.Name.LocalName == "TotalTime" && el.Value.Trim() == "0"))
                    r.Items.Add(new MetaItem(f.Label, el.Value.Trim(), f.Sensitive));
        }
        int comments = zip.Entries.Where(x => Regex.IsMatch(x.FullName, @"^(word/comments\.xml|xl/comments\d*\.xml|ppt/comments/.+\.xml|xl/threadedComments/.+\.xml)$")).Sum(x => CountTags(x, "comment"));
        if (comments > 0) r.Items.Add(new MetaItem("Commentaires dans le document", $"{comments} (visibles par le destinataire)", true));
        var doc = zip.GetEntry("word/document.xml");
        if (doc != null)
        {
            int changes = CountTags(doc, "ins") + CountTags(doc, "del");
            if (changes > 0) r.Items.Add(new MetaItem("Modifications suivies non acceptées", $"{changes} (l'ancien texte reste lisible)", true));
        }
        if (zip.Entries.Any(x => x.FullName.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith("item1.xml")))
            r.Items.Add(new MetaItem("Données personnalisées", "présentes (souvent liées à SharePoint / l'entreprise)", false));
    }

    private static int CountTags(ZipArchiveEntry e, string local)
    {
        using var s = e.Open(); using var sr = new StreamReader(s);
        return Regex.Matches(sr.ReadToEnd(), $@"<(\w+:)?{local}[\s>]").Count;
    }

    // ─── PDF ───
    private static readonly (string Key, string Label, bool Sensitive)[] PdfKeys =
    {
        ("Author", "Auteur", true), ("Creator", "Logiciel d'origine", false), ("Producer", "Producteur", false), ("Title", "Titre", false),
        ("Subject", "Sujet", false), ("Keywords", "Mots-clés", false), ("CreationDate", "Créé le", false), ("ModDate", "Modifié le", false),
    };

    internal static void InspectPdf(string path, MetaReport r)
    {
        var fi = new FileInfo(path);
        byte[] head = ReadHead(path, 2 * 1024 * 1024);
        byte[] tail = Array.Empty<byte>();
        if (fi.Length > head.Length)
        {
            using var fs = File.OpenRead(path);
            fs.Seek(-Math.Min(512 * 1024, fi.Length - head.Length), SeekOrigin.End);
            tail = new byte[fs.Length - fs.Position]; int n = 0, k; while (n < tail.Length && (k = fs.Read(tail, n, tail.Length - n)) > 0) n += k;
        }
        string text = Encoding.Latin1.GetString(head) + Encoding.Latin1.GetString(tail);
        foreach (var (key, label, sens) in PdfKeys)
        {
            var m = Regex.Match(text, $@"/{key}\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>)");
            if (!m.Success) continue;
            string v = DecodePdfString(m.Groups[1].Value);
            if (key.EndsWith("Date") && Regex.Match(v, @"D:(\d{4})(\d{2})(\d{2})(\d{2})?(\d{2})?") is { Success: true } dm)
                v = $"{dm.Groups[3].Value}/{dm.Groups[2].Value}/{dm.Groups[1].Value}" + (dm.Groups[4].Success ? $" {dm.Groups[4].Value}:{dm.Groups[5].Value}" : "");
            if (v.Trim().Length > 0) r.Items.Add(new MetaItem(label, v.Trim(), sens));
        }
        InspectXmp(head, r);
    }

    internal static string DecodePdfString(string s)
    {
        if (s.StartsWith("<"))
        {
            string hex = Regex.Replace(s.Trim('<', '>'), @"\s", "");
            if (hex.Length % 2 == 1) hex += "0";
            var bytes = Convert.FromHexString(hex);
            return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.Latin1.GetString(bytes);
        }
        string inner = s.Length >= 2 ? s[1..^1] : s;
        var sb = new StringBuilder();
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c != '\\' || i + 1 >= inner.Length) { sb.Append(c); continue; }
            char n = inner[++i];
            if (n is >= '0' and <= '7')
            {
                int len = 1; while (len < 3 && i + len < inner.Length && inner[i + len] is >= '0' and <= '7') len++;
                sb.Append((char)Convert.ToInt32(inner.Substring(i, len), 8)); i += len - 1;
            }
            else sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => n });
        }
        string res = sb.ToString();
        if (res.Length >= 2 && res[0] == 'þ' && res[1] == 'ÿ')   // UTF-16 BE entre parenthèses
            res = Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(res[2..]));
        return res;
    }

    /// <summary>Tableau CSV (une ligne par fichier, une colonne par information ; « ; » pour Excel en français, BOM UTF-8).</summary>
    public static string ToCsv(IEnumerable<MetaReport> reports)
    {
        var list = reports.ToList();
        var labels = list.SelectMany(r => r.Items.Select(i => i.Label)).Distinct().ToList();
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        var sb = new StringBuilder("﻿");
        sb.AppendLine(string.Join(";", new[] { "Fichier", "Dossier", "Type", "Infos personnelles", "Latitude", "Longitude" }.Concat(labels).Select(Q)));
        foreach (var r in list)
        {
            var cells = new List<string>
            {
                System.IO.Path.GetFileName(r.Path), System.IO.Path.GetDirectoryName(r.Path) ?? "", r.Kind, r.SensitiveCount.ToString(),
                r.Gps?.Lat.ToString("0.000000", CultureInfo.InvariantCulture) ?? "", r.Gps?.Lon.ToString("0.000000", CultureInfo.InvariantCulture) ?? ""
            };
            cells.AddRange(labels.Select(l => r.Items.FirstOrDefault(i => i.Label == l)?.Value ?? ""));
            sb.AppendLine(string.Join(";", cells.Select(Q)));
        }
        return sb.ToString();
    }

    // ─── Nettoyage (copie) ───
    public static string CleanCopyPath(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path)!, name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        string p = System.IO.Path.Combine(dir, $"{name} (sans métadonnées){ext}");
        for (int i = 2; File.Exists(p); i++) p = System.IO.Path.Combine(dir, $"{name} (sans métadonnées {i}){ext}");
        return p;
    }

    /// <summary>Crée une copie nettoyée à côté de l'original (jamais modifié). Renvoie le chemin de la copie.</summary>
    public static string CleanCopy(string path, string? destination = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string dest = destination ?? CleanCopyPath(path);
        if (ext is ".jpg" or ".jpeg" or ".jfif") File.WriteAllBytes(dest, CleanJpeg(File.ReadAllBytes(path)));
        else if (ext == ".png") File.WriteAllBytes(dest, CleanPng(File.ReadAllBytes(path)));
        else if (OfficeExt.Contains(ext)) { File.Copy(path, dest, false); CleanOffice(dest); }
        else throw new NotSupportedException("nettoyage non pris en charge pour ce type de fichier");
        return dest;
    }

    internal static byte[] CleanJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new InvalidDataException("JPEG invalide");
        var probe = new MetaReport(); int t = FindTiff(d); int orientation = 1;
        if (t >= 0) { ParseExif(d, t, probe); int.TryParse(probe.Items.FirstOrDefault(i => i.Label == "Orientation")?.Value, out orientation); }
        using var ms = new MemoryStream();
        ms.Write(d, 0, 2);
        if (orientation is > 1 and <= 8) ms.Write(MinimalExif(orientation));   // sinon la photo s'afficherait pivotée
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) throw new InvalidDataException("structure JPEG inattendue");
            byte marker = d[p + 1];
            if (marker == 0xDA) { ms.Write(d, p, d.Length - p); break; }   // image : copiée telle quelle
            int len = d[p + 2] << 8 | d[p + 3];
            if (len < 2 || p + 2 + len > d.Length) throw new InvalidDataException("segment JPEG tronqué");
            bool drop = marker is 0xE1 or 0xED or 0xFE;   // EXIF / XMP, IPTC (Photoshop), commentaire ; ICC (APP2) gardé pour les couleurs
            if (!drop) ms.Write(d, p, 2 + len);
            p += 2 + len;
        }
        return ms.ToArray();
    }

    private static byte[] MinimalExif(int orientation)
    {
        var tiff = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0 };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        int len = payload.Length + 2;
        return new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload).ToArray();
    }

    internal static byte[] CleanPng(byte[] d)
    {
        if (d.Length < 8 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') throw new InvalidDataException("PNG invalide");
        using var ms = new MemoryStream();
        ms.Write(d, 0, 8);
        foreach (var (type, start, len) in PngChunks(d))
            if (type is not ("tEXt" or "iTXt" or "zTXt" or "eXIf" or "tIME")) ms.Write(d, start - 8, len + 12);
        return ms.ToArray();
    }

    internal static void CleanOffice(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var part in new[] { "docProps/core.xml", "docProps/app.xml" })
        {
            var e = zip.GetEntry(part); if (e == null) continue;
            XDocument x; using (var s = e.Open()) x = XDocument.Load(s);
            foreach (var el in x.Root?.Elements().ToList() ?? new())
                switch (el.Name.LocalName)
                {
                    case "creator" or "lastModifiedBy" or "title" or "subject" or "keywords" or "description" or "category" or "Company" or "Manager" or "Template" or "HyperlinkBase": el.Value = ""; break;
                    case "lastPrinted": el.Remove(); break;
                    case "revision": el.Value = "1"; break;
                    case "TotalTime": el.Value = "0"; break;
                }
            e.Delete();
            var ne = zip.CreateEntry(part);
            using var w = ne.Open();
            x.Save(w);
        }
    }
}
