<#
.SYNOPSIS
    Shared code for the Usenet stub (docs/golden-path.md, step 6): yEnc, a small NNTP server and a small Newznab server.

.DESCRIPTION
    Dot-source this file. It compiles one C# helper with Add-Type, which works on Windows PowerShell 5.1 (the
    only PowerShell in the golden-path VM). The C# is written for the 5.1 compiler (C# 5): no string
    interpolation, no null-conditional member access, no expression-bodied members.

    Everything listens on 127.0.0.1 only and uses no real credential: any login is accepted.
#>

if (-not ('Golden.Yenc' -as [type])) {
    $source = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Golden
{
    public static class Crc32
    {
        private static readonly uint[] Table = Build();
        private static uint[] Build()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
                table[i] = c;
            }
            return table;
        }
        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            uint c = crc ^ 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++) { c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8); }
            return c ^ 0xFFFFFFFFu;
        }
        public static uint Of(byte[] data, int offset, int count) { return Update(0, data, offset, count); }
    }

    public static class Yenc
    {
        // The body of a yEnc part: =ybegin, =ypart, the encoded bytes, =yend. Lines end CRLF.
        public static byte[] EncodePart(byte[] data, int offset, int count, long begin, int part, int total, long totalSize, string name, uint wholeCrc, bool last)
        {
            MemoryStream ms = new MemoryStream(count + count / 50 + 512);
            Write(ms, "=ybegin part=" + part + " total=" + total + " line=128 size=" + totalSize + " name=" + name + "\r\n");
            Write(ms, "=ypart begin=" + begin + " end=" + (begin + count - 1) + "\r\n");
            int col = 0;
            for (int i = offset; i < offset + count; i++)
            {
                byte o = (byte)((data[i] + 42) & 0xFF);
                bool critical = o == 0 || o == 10 || o == 13 || o == 61
                    || (col == 0 && (o == 46 || o == 9 || o == 32))
                    || (col == 127 && (o == 9 || o == 32));
                if (critical)
                {
                    if (col >= 127) { ms.WriteByte(13); ms.WriteByte(10); col = 0; }
                    ms.WriteByte(61); ms.WriteByte((byte)((o + 64) & 0xFF)); col += 2;
                }
                else { ms.WriteByte(o); col++; }
                if (col >= 128) { ms.WriteByte(13); ms.WriteByte(10); col = 0; }
            }
            if (col > 0) { ms.WriteByte(13); ms.WriteByte(10); }
            uint partCrc = Crc32.Of(data, offset, count);
            string end = "=yend size=" + count + " part=" + part + " pcrc32=" + partCrc.ToString("x8", CultureInfo.InvariantCulture);
            if (last) { end += " crc32=" + wholeCrc.ToString("x8", CultureInfo.InvariantCulture); }
            Write(ms, end + "\r\n");
            return ms.ToArray();
        }

        // Back to the original bytes. Header and trailer lines (=ybegin, =ypart, =yend) are skipped.
        public static byte[] Decode(byte[] body)
        {
            MemoryStream ms = new MemoryStream(body.Length);
            int i = 0;
            while (i < body.Length)
            {
                int lineEnd = i;
                while (lineEnd < body.Length && body[lineEnd] != 10) { lineEnd++; }
                bool control = lineEnd - i >= 2 && body[i] == 61 && body[i + 1] == 121;
                if (!control)
                {
                    int j = i;
                    while (j < lineEnd)
                    {
                        byte b = body[j];
                        if (b == 13) { j++; continue; }
                        if (b == 61 && j + 1 < lineEnd) { j++; b = (byte)((body[j] - 64) & 0xFF); }
                        ms.WriteByte((byte)((b - 42) & 0xFF));
                        j++;
                    }
                }
                i = lineEnd + 1;
            }
            return ms.ToArray();
        }

        private static void Write(Stream s, string text) { byte[] b = Encoding.ASCII.GetBytes(text); s.Write(b, 0, b.Length); }
    }

    // A news server that serves articles from <Root>\articles\<message-id>.art (headers, a blank line, the body).
    public class NntpServer
    {
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool stopping;
        private long served;
        private long missed;
        private long connections;
        public string Root { get; set; }
        public int Port { get; set; }
        // A file of message-ids (one a line) that are never answered: the connection just waits, as a news server
        // that stalls on its last article does. The file is read on every request, so an id can be added mid-run.
        public string HangFile { get; set; }
        // A pause before each article listed in SlowFile (same format as HangFile), in milliseconds: a slow news server.
        public int ArticleDelayMs { get; set; }
        public string SlowFile { get; set; }
        public long ArticlesServed { get { return Interlocked.Read(ref served); } }
        public long ArticlesMissed { get { return Interlocked.Read(ref missed); } }
        public long Connections { get { return Interlocked.Read(ref connections); } }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop() { stopping = true; try { listener.Stop(); } catch (Exception) { } }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch (Exception) { return; }
                Interlocked.Increment(ref connections);
                Thread t = new Thread(delegate () { Serve(client); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private bool IsHung(string id)
        {
            try
            {
                string path = Path.Combine(Root, "hang.txt");
                if (!File.Exists(path)) { return false; }
                foreach (string line in File.ReadAllLines(path)) { if (line.Trim() == id) { return true; } }
            }
            catch (Exception) { }
            return false;
        }

        private static string SafeId(string raw)
        {
            if (raw == null) { return null; }
            string id = raw.Trim();
            if (id.StartsWith("<")) { id = id.Substring(1); }
            if (id.EndsWith(">")) { id = id.Substring(0, id.Length - 1); }
            if (id.Length == 0 || id.Length > 200) { return null; }
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '@' || c == '_' || c == '-';
                if (!ok) { return null; }
            }
            return id;
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    Stream stream = client.GetStream();
                    Send(stream, "200 Golden Usenet stub ready (no posting)\r\n");
                    while (!stopping)
                    {
                        string line = ReadLine(stream);
                        if (line == null) { return; }
                        string[] parts = line.Trim().Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) { continue; }
                        string cmd = parts[0].ToUpperInvariant();
                        string arg = parts.Length > 1 ? parts[1] : "";
                        if (cmd == "QUIT") { Send(stream, "205 Goodbye\r\n"); return; }
                        else if (cmd == "CAPABILITIES") { Send(stream, "101 Capability list:\r\nVERSION 2\r\nREADER\r\nAUTHINFO USER\r\nIMPLEMENTATION GoldenStub\r\n.\r\n"); }
                        else if (cmd == "MODE") { Send(stream, "200 Reader mode, posting prohibited\r\n"); }
                        else if (cmd == "AUTHINFO")
                        {
                            if (arg.StartsWith("USER", StringComparison.OrdinalIgnoreCase)) { Send(stream, "381 Password required\r\n"); }
                            else { Send(stream, "281 Authentication accepted\r\n"); }
                        }
                        else if (cmd == "GROUP") { Send(stream, "211 1000 1 1000 " + (arg.Length > 0 ? arg : "alt.binaries.golden") + "\r\n"); }
                        else if (cmd == "DATE") { Send(stream, "111 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "\r\n"); }
                        else if (cmd == "STAT" || cmd == "ARTICLE" || cmd == "BODY" || cmd == "HEAD")
                        {
                            string id = SafeId(arg);
                            if (id != null && IsListed(HangFile, id)) { Thread.Sleep(Timeout.Infinite); }
                            if (ArticleDelayMs > 0 && id != null && cmd != "STAT" && IsListed(SlowFile, id)) { Thread.Sleep(ArticleDelayMs); }
                            // An article named in <Root>\hang.txt is never answered, as a news server that stalls on one article does:
                            // a download sticks just short of the end (the rc.30.19 "Stuckfilm" case).
                            if (id != null && IsHung(id)) { while (!stopping) { Thread.Sleep(1000); } return; }
                            string file = id == null ? null : Path.Combine(Path.Combine(Root, "articles"), id + ".art");
                            if (file == null || !File.Exists(file))
                            {
                                Interlocked.Increment(ref missed);
                                Send(stream, "430 No such article\r\n");
                                continue;
                            }
                            if (cmd == "STAT") { Send(stream, "223 0 <" + id + ">\r\n"); continue; }
                            byte[] all = File.ReadAllBytes(file);
                            int split = IndexOfBlankLine(all);
                            int headEnd = split < 0 ? all.Length : split;
                            int bodyStart = split < 0 ? all.Length : split + 4;
                            string code = cmd == "ARTICLE" ? "220" : cmd == "HEAD" ? "221" : "222";
                            MemoryStream outMs = new MemoryStream(all.Length + 512);
                            WriteTo(outMs, code + " 0 <" + id + ">\r\n");
                            if (cmd == "ARTICLE" || cmd == "HEAD") { DotStuff(outMs, all, 0, headEnd); }
                            if (cmd == "ARTICLE") { WriteTo(outMs, "\r\n"); }
                            if (cmd == "ARTICLE" || cmd == "BODY") { DotStuff(outMs, all, bodyStart, all.Length - bodyStart); }
                            WriteTo(outMs, ".\r\n");
                            byte[] payload = outMs.ToArray();
                            stream.Write(payload, 0, payload.Length);
                            stream.Flush();
                            Interlocked.Increment(ref served);
                        }
                        else { Send(stream, "500 Command not recognised\r\n"); }
                    }
                }
            }
            catch (Exception) { }
        }

        private static bool IsListed(string path, string id)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }
            try
            {
                foreach (string line in File.ReadAllLines(path)) { if (line.Trim().Trim('<', '>') == id) { return true; } }
            }
            catch (IOException) { }
            return false;
        }

        private static int IndexOfBlankLine(byte[] b)
        {
            for (int i = 0; i + 3 < b.Length; i++)
            {
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) { return i; }
            }
            return -1;
        }

        // Lines that start with "." are doubled; the text always ends with CRLF.
        private static void DotStuff(Stream s, byte[] data, int offset, int count)
        {
            bool lineStart = true;
            byte last = 0;
            for (int i = offset; i < offset + count; i++)
            {
                byte b = data[i];
                if (lineStart && b == 46) { s.WriteByte(46); }
                s.WriteByte(b);
                lineStart = b == 10;
                last = b;
            }
            if (count > 0 && last != 10) { s.WriteByte(13); s.WriteByte(10); }
        }

        private static string ReadLine(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) { return sb.Length == 0 ? null : sb.ToString(); }
                if (b == 10) { return sb.ToString().TrimEnd('\r'); }
                if (sb.Length < 4096) { sb.Append((char)b); }
            }
        }

        private static void WriteTo(Stream s, string text) { byte[] b = Encoding.ASCII.GetBytes(text); s.Write(b, 0, b.Length); }
        private static void Send(Stream s, string text) { WriteTo(s, text); s.Flush(); }
    }

    // A Newznab API: t=caps, t=search / movie / tvsearch / tv, t=get. Any API key (or none) is accepted.
    public class NewznabServer
    {
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool stopping;
        private long requests;
        public int Port { get; set; }
        public string ReleaseName { get; set; }
        public string ReleaseId { get; set; }
        public long SizeBytes { get; set; }
        public string NzbPath { get; set; }
        public long Requests { get { return Interlocked.Read(ref requests); } }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop() { stopping = true; try { listener.Stop(); } catch (Exception) { } }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch (Exception) { return; }
                Thread t = new Thread(delegate () { Serve(client); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 15000;
                    Stream stream = client.GetStream();
                    string requestLine = null;
                    while (true)
                    {
                        StringBuilder sb = new StringBuilder();
                        int b;
                        while ((b = stream.ReadByte()) >= 0 && b != 10) { if (sb.Length < 8192) { sb.Append((char)b); } }
                        if (b < 0 && sb.Length == 0) { return; }
                        string line = sb.ToString().TrimEnd('\r');
                        if (requestLine == null) { requestLine = line; }
                        if (line.Length == 0) { break; }
                    }
                    Interlocked.Increment(ref requests);
                    string[] words = requestLine.Split(' ');
                    string target = words.Length > 1 ? words[1] : "/";
                    string path = target;
                    string queryText = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); queryText = target.Substring(q + 1); }
                    Dictionary<string, string> query = ParseQuery(queryText);
                    string host = "127.0.0.1:" + Port;
                    if (path == "/health") { Respond(stream, 200, "text/plain", Encoding.UTF8.GetBytes("ok")); return; }
                    if (path != "/api" && path != "/") { Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
                    string t = Get(query, "t").ToLowerInvariant();
                    if (t == "caps") { Respond(stream, 200, "application/xml", Encoding.UTF8.GetBytes(Caps())); }
                    else if (t == "get")
                    {
                        if (Get(query, "id") != ReleaseId || !File.Exists(NzbPath)) { Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("no such nzb")); }
                        else { Respond(stream, 200, "application/x-nzb", File.ReadAllBytes(NzbPath)); }
                    }
                    else if (t == "search" || t == "movie" || t == "tvsearch" || t == "tv" || t == "")
                    {
                        Respond(stream, 200, "application/rss+xml", Encoding.UTF8.GetBytes(Feed(t, query, host)));
                    }
                    else { Respond(stream, 200, "application/xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><error code=\"202\" description=\"No such function\"/>")); }
                }
            }
            catch (Exception) { }
        }

        private static string Get(Dictionary<string, string> q, string key) { string v; return q.TryGetValue(key, out v) ? v : ""; }

        private static Dictionary<string, string> ParseQuery(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in text.Split(new char[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                try { d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch (Exception) { }
            }
            return d;
        }

        private static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }

        private string Caps()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<caps>\n"
                + "<server version=\"1.0\" title=\"Golden Newznab stub\" strapline=\"Local test indexer\" email=\"nobody@example.invalid\" url=\"http://127.0.0.1:" + Port + "/\"/>\n"
                + "<limits max=\"100\" default=\"100\"/>\n"
                + "<registration available=\"no\" open=\"no\"/>\n"
                + "<searching>\n<search available=\"yes\" supportedParams=\"q\"/>\n"
                + "<tv-search available=\"yes\" supportedParams=\"q,season,ep,tvdbid\"/>\n"
                + "<movie-search available=\"yes\" supportedParams=\"q,imdbid,tmdbid\"/>\n</searching>\n"
                + "<categories>\n<category id=\"2000\" name=\"Movies\"><subcat id=\"2040\" name=\"Movies/HD\"/><subcat id=\"2045\" name=\"Movies/UHD\"/></category>\n"
                + "<category id=\"5000\" name=\"TV\"><subcat id=\"5040\" name=\"TV/HD\"/></category>\n</categories>\n</caps>\n";
        }

        private bool Matches(string t, Dictionary<string, string> query)
        {
            if (t == "tvsearch" || t == "tv") { return false; }
            string cat = Get(query, "cat");
            if (cat.Length > 0 && cat.IndexOf("2", StringComparison.Ordinal) < 0) { return false; }
            string text = Get(query, "q");
            if (text.Length == 0) { return true; }
            string name = ReleaseName.ToLowerInvariant();
            bool anyWord = false;
            foreach (string word in text.ToLowerInvariant().Split(new char[] { ' ', '.', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length < 3) { continue; }
                anyWord = true;
                if (name.IndexOf(word, StringComparison.Ordinal) >= 0) { return true; }
            }
            return !anyWord;
        }

        private string Feed(string t, Dictionary<string, string> query, string host)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<rss version=\"2.0\" xmlns:atom=\"http://www.w3.org/2005/Atom\" xmlns:newznab=\"http://www.newznab.com/DTD/2010/feeds/attributes/\">\n<channel>\n");
            sb.Append("<title>Golden Newznab stub</title>\n<description>Local test indexer</description>\n<link>http://" + host + "/</link>\n");
            bool match = Matches(t, query);
            sb.Append("<newznab:response offset=\"0\" total=\"" + (match ? 1 : 0) + "\"/>\n");
            if (match)
            {
                string key = Get(query, "apikey");
                string get = "http://" + host + "/api?t=get&id=" + Uri.EscapeDataString(ReleaseId) + (key.Length > 0 ? "&apikey=" + Uri.EscapeDataString(key) : "");
                string posted = DateTime.UtcNow.AddDays(-2).ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";
                sb.Append("<item>\n<title>" + Esc(ReleaseName) + "</title>\n");
                sb.Append("<guid isPermaLink=\"false\">" + Esc(ReleaseId) + "</guid>\n");
                sb.Append("<link>" + Esc(get) + "</link>\n");
                sb.Append("<comments>" + Esc("http://" + host + "/details/" + ReleaseId) + "</comments>\n");
                sb.Append("<pubDate>" + posted + "</pubDate>\n");
                sb.Append("<category>Movies &gt; HD</category>\n");
                sb.Append("<description>" + Esc(ReleaseName) + "</description>\n");
                sb.Append("<enclosure url=\"" + Esc(get) + "\" length=\"" + SizeBytes + "\" type=\"application/x-nzb\"/>\n");
                sb.Append("<newznab:attr name=\"category\" value=\"2000\"/>\n<newznab:attr name=\"category\" value=\"2040\"/>\n");
                sb.Append("<newznab:attr name=\"size\" value=\"" + SizeBytes + "\"/>\n");
                sb.Append("<newznab:attr name=\"grabs\" value=\"3\"/>\n");
                sb.Append("<newznab:attr name=\"files\" value=\"1\"/>\n");
                sb.Append("<newznab:attr name=\"usenetdate\" value=\"" + posted + "\"/>\n");
                sb.Append("</item>\n");
            }
            sb.Append("</channel>\n</rss>\n");
            return sb.ToString();
        }

        private static void Respond(Stream s, int status, string contentType, byte[] body)
        {
            bool text = contentType.StartsWith("text") || contentType.Contains("xml") || contentType.Contains("rss");
            string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\nContent-Type: " + contentType + (text ? "; charset=utf-8" : "")
                + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }
    }
}
'@
    Add-Type -TypeDefinition $source -Language CSharp
}
