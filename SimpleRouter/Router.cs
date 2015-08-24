using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleRouter
{
    public class Router
    {
        private HttpListener listener;
        private List<Route> routes = new List<Route>();

        public Router()
        {

        }

        public void Start(int port)
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://+:" + port + "/");

            listener.Start();
            Listen();
        }

        private async void Listen()
        {
            while (true)
            {
                var context = await listener.GetContextAsync();
                Task.Factory.StartNew(() => Process(context)).FireAndForget();
            }
        }

        public void Stop()
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }

        public void Route(string verb, string path, Action<Request, Response> handler)
        {
            routes.Add(new Route(verb, path, async (req, res) => { await Task.Run(() => handler(req, res)); }));
        }

        public void Route(string verb, string path, Func<Request, Response, Task> handler)
        {
            routes.Add(new Route(verb, path, handler));
        }

        private void GotCallback(IAsyncResult ar)
        {
            var ctx = listener.EndGetContext(ar);
            listener.BeginGetContext(GotCallback, null);
            Process(ctx);
        }

        private async void Process(HttpListenerContext ctx)
        {
            Request request = new Request(ctx);
            Response response = new Response(ctx);

            foreach (Route route in routes)
            {
                Match match = route.Regex.Match(request.URL.Path);
                if (match.Success)
                {
                    request.Keys = route.Regex.GetGroupNames().ToDictionary(g => g, g => match.Groups[g].Value);
                    await route.Callback(request, response);
                    if (response.Complete)
                        return;
                }
            }

            response.StatusCode(404);
            response.Send("Not found");
        }
    }

    public static class TaskExtensions
    {
        public static void FireAndForget(this Task task) { }
    }

    public class Route
    {
        public string Method { get; protected set; }
        public Regex Regex { get; protected set; }
        public string Path { get; protected set; }
        public Func<Request, Response, Task> Callback { get; protected set; }

        public static Regex namedParameterKey = new Regex(":([a-z_]+)");

        public Route(string method, string path, Func<Request, Response, Task> callback)
        {
            Method = method;
            Path = path;
            Callback = callback;
            Regex = RegexFromPath(path);
        }

        private static Regex RegexFromPath(string path)
        {
            string regexString = namedParameterKey.Replace(path, (m) =>
            {
                return "(?<" + m.Groups[1].Value + ">[^/]+)";
            });

            return new Regex("^" + regexString.Replace("*", ".*") + "$");
        }
    }

    public class URL
    {
        public string Scheme { get; set; }
        public string HostName { get; set; }
        public int? Port { get; set; }
        public string BasePath { get; set; }
        public string Path { get; set; }
        public string Query { get; set; }

        public URL()
        {
        }

        public URL(Uri uri)
        {
            string absolutePath = uri.AbsolutePath.TrimEnd('/');
            absolutePath = absolutePath == "" ? "/" : absolutePath;

            Scheme = uri.Scheme;
            HostName = uri.Host;
            Port = uri.IsDefaultPort ? null : (int?)uri.Port;
            BasePath = absolutePath;
            Path = UrlDecode(absolutePath, Encoding.UTF8);
            Query = uri.Query;
        }

        private static void WriteCharBytes(IList buf, char ch, Encoding e)
        {
            if (ch > 255)
            {
                foreach (byte b in e.GetBytes(new char[] { ch }))
                    buf.Add(b);
            }
            else
                buf.Add((byte)ch);
        }

        private static int GetInt(byte b)
        {
            char c = (char)b;
            if (c >= '0' && c <= '9')
                return c - '0';

            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;

            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;

            return -1;
        }

        private static int GetChar(string str, int offset, int length)
        {
            int val = 0;
            int end = length + offset;
            for (int i = offset; i < end; i++)
            {
                char c = str[i];
                if (c > 127)
                    return -1;

                int current = GetInt((byte)c);
                if (current == -1)
                    return -1;
                val = (val << 4) + current;
            }

            return val;
        }

        private static string UrlDecode(string s, Encoding e)
        {
            if (null == s)
                return null;

            if (s.IndexOf('%') == -1 && s.IndexOf('+') == -1)
                return s;

            if (e == null)
                e = Encoding.UTF8;

            long len = s.Length;
            var bytes = new List<byte>();
            int xchar;
            char ch;

            for (int i = 0; i < len; i++)
            {
                ch = s[i];
                if (ch == '%' && i + 2 < len && s[i + 1] != '%')
                {
                    if (s[i + 1] == 'u' && i + 5 < len)
                    {
                        // unicode hex sequence
                        xchar = GetChar(s, i + 2, 4);
                        if (xchar != -1)
                        {
                            WriteCharBytes(bytes, (char)xchar, e);
                            i += 5;
                        }
                        else
                            WriteCharBytes(bytes, '%', e);
                    }
                    else if ((xchar = GetChar(s, i + 1, 2)) != -1)
                    {
                        WriteCharBytes(bytes, (char)xchar, e);
                        i += 2;
                    }
                    else
                    {
                        WriteCharBytes(bytes, '%', e);
                    }
                    continue;
                }

                if (ch == '+')
                    WriteCharBytes(bytes, ' ', e);
                else
                    WriteCharBytes(bytes, ch, e);
            }

            byte[] buf = bytes.ToArray();
            bytes = null;
            return e.GetString(buf);
        }
    }

    public class Request
    {
        public URL URL { get; set; }
        public Dictionary<string, string> Keys { get; set; }
        public NameValueCollection Query { get; set; }

        public Request(HttpListenerContext context)
        {
            URL = new SimpleRouter.URL(context.Request.Url);
            Keys = new Dictionary<string, string>();
            Query = context.Request.QueryString;
        }
    }

    public class Response
    {
        private HttpListenerContext context;
        public bool Complete { get; protected set; }

        public Response(HttpListenerContext context)
        {
            this.context = context;
            Complete = false;
        }

        public void ContentType(string contentType)
        {
            context.Response.ContentType = contentType;
        }

        public void StatusCode(HttpStatusCode statusCode)
        {
            StatusCode((int)statusCode);
        }

        public void StatusCode(int statusCode)
        {
            context.Response.StatusCode = statusCode;
        }

        public void SendJson(object obj, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            StatusCode(statusCode);
            ContentType("application/json");
            Send(JsonConvert.SerializeObject(obj));
        }

        public void Send(string text)
        {

            Send(System.Text.Encoding.UTF8.GetBytes(text));
        }

        public void Send(byte[] data)
        {
            if (Complete)
                throw new Exception("Attempting to send on a completed response.");

            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.OutputStream.Flush();
            context.Response.OutputStream.Close();
            Complete = true;
        }
    }
}
