using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
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
                if (!(route.Method == "*" || route.Method.Equals(request.Method, StringComparison.InvariantCultureIgnoreCase)))
                    continue;

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
            Path = System.Web.HttpUtility.UrlDecode(absolutePath);
            Query = uri.Query;
        }
    }

    public class Request
    {
        public URL URL { get; set; }
        public string Method { get; set; }
        public Dictionary<string, string> Keys { get; set; }
        public NameValueCollection Query { get; set; }
        public Stream InputStream { get; set; }

        public Request(HttpListenerContext context)
        {
            URL = new SimpleRouter.URL(context.Request.Url);
            Method = context.Request.HttpMethod;
            Keys = new Dictionary<string, string>();
            Query = context.Request.QueryString;
            InputStream = context.Request.InputStream;
        }

        public T DeserializeJSON<T>()
        {
            string body = new StreamReader(InputStream).ReadToEnd();
            return JsonConvert.DeserializeObject<T>(body);
        }
    }

    public class Response
    {
        private HttpListenerContext context;
        public bool Complete { get; protected set; }
        public WebHeaderCollection Headers { get; protected set; }

        public Response(HttpListenerContext context)
        {
            this.context = context;
            Complete = false;
            Headers = context.Response.Headers;
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
