using Microsoft.AspNetCore.Http.Features;
using System.Text;

namespace Kintone.Mock
{
    /// <summary>モックの kintone REST API の入口 (/k/...) を結び付ける。</summary>
    public static class MockKintoneEndpoints
    {
        public static IEndpointRouteBuilder MapMockKintone(this IEndpointRouteBuilder app, MockKintone mock)
        {
            async Task Handle(HttpContext context)
            {
                var request = await ToMockRequestAsync(context);
                MockResponse response;
                try
                {
                    response = await mock.HandleAsync(request, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                if (response.Abort)
                {
                    context.Abort();
                    return;
                }
                context.Response.StatusCode = response.Status;
                foreach (var header in response.Headers) context.Response.Headers[header.Key] = header.Value;
                if (response.Body != null)
                {
                    context.Response.ContentType = response.ContentType + "; charset=utf-8";
                    await context.Response.WriteAsync(response.Body, Encoding.UTF8);
                }
            }

            app.Map(MockKintone.BasePath + "/{**path}", Handle);
            return app;
        }

        static async Task<MockRequest> ToMockRequestAsync(HttpContext context)
        {
            var http = context.Request;
            var query = http.Query.SelectMany(e => e.Value.Select(v => new KeyValuePair<string, string>(e.Key, v ?? string.Empty))).ToList();

            string body;
            using (var reader = new StreamReader(http.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }
            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (string.IsNullOrEmpty(rawTarget) || !rawTarget.StartsWith('/')) rawTarget = http.PathBase + http.Path.ToUriComponent() + http.QueryString;

            static string? Header(HttpRequest r, string name) => r.Headers[name].Count == 0 ? null : r.Headers[name].ToString();

            return new MockRequest
            {
                Method = http.Method,
                Path = http.Path.Value ?? string.Empty,
                Query = query,
                PathAndQuery = rawTarget,
                Body = body.Length == 0 ? null : body,
                ContentType = http.ContentType,
                ApiToken = Header(http, "X-Cybozu-API-Token"),
                CybozuAuthorization = Header(http, "X-Cybozu-Authorization"),
                MethodOverride = Header(http, "X-HTTP-Method-Override"),
            };
        }
    }
}
