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
                if (response.BinaryBody != null)
                {
                    context.Response.ContentType = response.ContentType;
                    await context.Response.Body.WriteAsync(response.BinaryBody);
                }
                else if (response.Body != null)
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

            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                await http.Body.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            //multipart (ファイルのアップロード) はバイト列のまま渡す。それ以外は UTF-8 の文字
            var isMultipart = http.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
            var body = isMultipart ? string.Empty : Encoding.UTF8.GetString(bytes);
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
                BodyBytes = bytes.Length == 0 ? null : bytes,
                ContentType = http.ContentType,
                ApiToken = Header(http, "X-Cybozu-API-Token"),
                CybozuAuthorization = Header(http, "X-Cybozu-Authorization"),
                MethodOverride = Header(http, "X-HTTP-Method-Override"),
            };
        }
    }
}
