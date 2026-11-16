using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    public class KintoneConnectionTest
    {
        FakeKintone _fake = null!;
        List<TimeSpan> _delays = null!;
        KintoneConnection _connection = null!;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeKintone();
            TestMetadata.AddForms(_fake);
            _delays = new List<TimeSpan>();
            _connection = TestConnections.Create(_fake, _delays);
        }

        [TearDown]
        public void TearDown() => _fake.Dispose();

        Task<KintoneConnection.RecordPage> ReadAsync(bool totalCount = false)
            => _connection.ReadRecordsAsync("Customer", new[] { "$id", "Name" }, "limit 1", totalCount);

        static JsonObject NameRecord(string name) => new() { ["Name"] = new JsonObject { ["value"] = name } };

        // ---------------------------------------------------------------- 認証

        [Test]
        public async Task APIトークンは全部をカンマでつないで付ける()
        {
            await ReadAsync();
            await _connection.CreateAsync("Customer", NameRecord("a"));
            foreach (var request in _fake.Requests)
            {
                Assert.That(request.Header("X-Cybozu-API-Token"), Is.EqualTo($"{TestConnections.TokenA},{TestConnections.TokenB}"));
                Assert.That(request.Header("X-Cybozu-Authorization"), Is.Null);
                Assert.That(request.Header("Authorization"), Is.Null);
            }
            Assert.That(_fake.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task パスワード認証はログイン名とパスワードのbase64()
        {
            var connection = TestConnections.Create(_fake, null, e =>
            {
                e.ApiTokens.Clear();
                e.User = "api-user";
                e.Password = "p@ss:wörd";
            });
            await connection.ReadRecordsAsync("Customer", Array.Empty<string>(), "limit 1", false);
            var request = _fake.Requests.Single();
            Assert.That(request.Header("X-Cybozu-Authorization"), Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("api-user:p@ss:wörd"))));
            Assert.That(request.Header("X-Cybozu-API-Token"), Is.Null);
        }

        // ---------------------------------------------------------------- 読み出し

        [Test]
        public async Task 読み出しはPOSTとMethodOverrideで本文にアプリとクエリとフィールドを書く()
        {
            _fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A") };

            var page = await _connection.ReadRecordsAsync("Customer", new[] { "$id", "Name" }, "Name = \"A\" order by $id asc limit 500", false);

            var request = _fake.Requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Url, Is.EqualTo(FakeKintone.ApiUrl + "/records.json"));
            Assert.That(request.Header("X-HTTP-Method-Override"), Is.EqualTo("GET"));
            Assert.That(request.Header("Content-Type"), Does.StartWith("application/json"));
            Assert.That(request.Json!.ToJsonString(), Is.EqualTo(new JsonObject
            {
                ["app"] = 11,
                ["query"] = "Name = \"A\" order by $id asc limit 500",
                ["fields"] = new JsonArray("$id", "Name"),
            }.ToJsonString()));
            Assert.That(request.Json!["query"]!.GetValue<string>(), Is.EqualTo("Name = \"A\" order by $id asc limit 500"));
            Assert.That(page.TotalCount, Is.Null);
            Assert.That(page.Records.Single().GetProperty("Name").GetProperty("value").GetString(), Is.EqualTo("A"));
        }

        [Test]
        public async Task フィールドを指定しなければfieldsを書かず件数はtotalCountで求める()
        {
            _fake.Records[TestMetadata.CustomerApp] = Enumerable.Range(1, 3).Select(i => KintoneHarness.CustomerRecord(i, "n")).ToList();
            var page = await _connection.ReadRecordsAsync("Customer", Array.Empty<string>(), "limit 1", true);
            Assert.That(_fake.Requests.Single().Body, Is.EqualTo("""{"app":11,"query":"limit 1","totalCount":true}"""));
            Assert.That(page.TotalCount, Is.EqualTo(3));
            Assert.That(page.Records, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ゲストスペースのURL()
        {
            var connection = TestConnections.Create(_fake, null, e => e.GuestSpaceId = 7);
            await connection.ReadRecordsAsync("Customer", Array.Empty<string>(), "limit 1", false);
            await connection.GetTableDefinitionAsync("Region");
            Assert.That(_fake.Requests.Select(e => e.Url), Is.EqualTo(new[]
            {
                "https://example.cybozu.com/k/guest/7/v1/records.json",
                "https://example.cybozu.com/k/guest/7/v1/app/form/fields.json?app=12",
            }));
        }

        [TestCase("\"12\"", 12)]
        [TestCase("12", 12)]
        [TestCase("\"0\"", 0)]
        public async Task 件数は文字でも数値でも読む(string count, long expected)
        {
            _fake.Override(e => e.IsRead, _ => FakeKintone.Status(HttpStatusCode.OK, $$"""{"records":[],"totalCount":{{count}}}"""));
            Assert.That((await ReadAsync(true)).TotalCount, Is.EqualTo(expected));
        }

        [TestCase("""{"records":[],"totalCount":null}""")]
        [TestCase("""{"records":[]}""")]
        [TestCase("""{"records":[],"totalCount":"-1"}""")]
        [TestCase("""{"records":[],"totalCount":"1.5"}""")]
        [TestCase("""{"records":[],"totalCount":"abc"}""")]
        public void 件数が読めなければ0にせずKintoneException(string body)
        {
            _fake.Override(e => e.IsRead, _ => FakeKintone.Status(HttpStatusCode.OK, body));
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync(true))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.Unknown));
            Assert.That(e.Message, Does.Contain("totalCount"));
        }

        [TestCase("""{"value":[]}""")]
        [TestCase("""{"records":{}}""")]
        [TestCase("[]")]
        [TestCase("<html></html>")]
        public void 読み出しの応答にrecordsが無ければKintoneException(string body)
        {
            _fake.Override(e => e.IsRead, _ => FakeKintone.Status(HttpStatusCode.OK, body));
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.Unknown));
        }

        // ---------------------------------------------------------------- 再試行

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.GatewayTimeout)]
        public async Task 読み出しは一時的な失敗をやり直す(HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(status), 2);
            var page = await ReadAsync();
            Assert.That(page.Records, Is.Empty);
            Assert.That(_fake.Requests, Has.Count.EqualTo(3));
            Assert.That(_delays, Has.Count.EqualTo(2));
            //待ち時間は 1 秒・2 秒 (+ 揺らぎ 250 ミリ秒未満)
            Assert.That(_delays[0], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)).And.LessThan(TimeSpan.FromSeconds(1.25)));
            Assert.That(_delays[1], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(2)).And.LessThan(TimeSpan.FromSeconds(2.25)));
        }

        [Test]
        public async Task RetryAfterがあればその分待つ()
        {
            _fake.Override(_ => true, _ =>
            {
                var response = FakeKintone.Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            });
            await ReadAsync();
            Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(7) }));
        }

        [Test]
        public void 読み出しのやり直しは4回まで()
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(HttpStatusCode.ServiceUnavailable, """{"message":"busy"}"""), 10);
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.ServiceUnavailable));
            Assert.That(e.Message, Is.EqualTo("kintone: busy"));
            Assert.That(_fake.Requests, Has.Count.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task 読み出しは通信の失敗もやり直す()
        {
            _fake.Throw(_ => true, () => new HttpRequestException("connection reset"), 3);
            await ReadAsync();
            Assert.That(_fake.Requests, Has.Count.EqualTo(4));
        }

        [Test]
        public void 読み出しの通信の失敗が続けばServiceUnavailableでホスト名を入れない()
        {
            var inner = new HttpRequestException("No such host is known. (example.cybozu.com:443)");
            _fake.Throw(_ => true, () => inner, 4);
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.ServiceUnavailable));
            Assert.That(e.Message, Is.EqualTo("Could not reach kintone (network error or timeout)."));
            Assert.That(e.InnerException, Is.SameAs(inner));
        }

        [Test]
        public void 読み出しで400などはやり直さない()
        {
            _fake.Override(_ => true, _ => FakeKintone.Error(HttpStatusCode.BadRequest, "GAIA_IQ11", "The query is invalid."), 10);
            Assert.ThrowsAsync<KintoneException>(() => ReadAsync());
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        // ---------------------------------------------------------------- エラーの分類

        [TestCase(HttpStatusCode.BadRequest, "CB_VA01", KintoneErrorKind.InvalidRequest)]
        [TestCase(HttpStatusCode.Unauthorized, "CB_WA01", KintoneErrorKind.Authentication)]
        [TestCase(HttpStatusCode.Forbidden, "GAIA_NO01", KintoneErrorKind.Authentication)]
        [TestCase(HttpStatusCode.Forbidden, "CB_NO02", KintoneErrorKind.Authentication)]
        [TestCase(HttpStatusCode.NotFound, "GAIA_RE01", KintoneErrorKind.NotFound)]
        [TestCase(HttpStatusCode.Conflict, "GAIA_CO02", KintoneErrorKind.InvalidRequest)]
        [TestCase(HttpStatusCode.TooManyRequests, "GAIA_TM01", KintoneErrorKind.ServiceUnavailable)]
        [TestCase((HttpStatusCode)520, "GAIA_AP01", KintoneErrorKind.NotFound)]
        [TestCase((HttpStatusCode)520, "GAIA_RE01", KintoneErrorKind.NotFound)]
        [TestCase((HttpStatusCode)520, "CB_IL02", KintoneErrorKind.InvalidRequest)]
        [TestCase(HttpStatusCode.InternalServerError, "", KintoneErrorKind.ServiceUnavailable)]
        public void エラーの分類(HttpStatusCode status, string code, KintoneErrorKind kind)
        {
            var body = new JsonObject { ["message"] = "失敗しました。" };
            if (code.Length != 0) body["code"] = code;
            _fake.Override(_ => true, _ => FakeKintone.Status(status, body.ToJsonString()), 10);
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.GetTableDefinitionAsync("Customer"))!;
            Assert.That(e.Kind, Is.EqualTo(kind));
            Assert.That(e.StatusCode, Is.EqualTo(status));
            Assert.That(e.ErrorCode, Is.EqualTo(code));
            Assert.That(e.Message, Is.EqualTo("kintone: 失敗しました。" + (code.Length == 0 ? string.Empty : $" (error {code})")));
        }

        [Test]
        public void 項目ごとのエラーは先頭の5件をメッセージに入れる()
        {
            var errors = new JsonObject();
            for (var i = 0; i < 7; i++) errors[$"record.F{i}.value"] = new JsonObject { ["messages"] = new JsonArray("必須です。", "長すぎます。") };
            errors["record.Other"] = "not an object";
            _fake.Override(_ => true, _ => FakeKintone.Error(HttpStatusCode.BadRequest, "CB_VA01", "入力内容が正しくありません。", errors));
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            Assert.That(e.Message, Is.EqualTo("kintone: 入力内容が正しくありません。 [F0: 必須です。 長すぎます。] [F1: 必須です。 長すぎます。] [F2: 必須です。 長すぎます。] " +
                "[F3: 必須です。 長すぎます。] [F4: 必須です。 長すぎます。] (error CB_VA01)"));
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
        }

        [TestCase("<html>denied</html>")]
        [TestCase("[\"x\"]")]
        [TestCase("")]
        public void JSONのオブジェクトでないエラー本文はステータスだけで分類する(string body)
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(HttpStatusCode.Unauthorized, body));
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.Authentication));
            Assert.That(e.Message, Is.EqualTo("kintone: HTTP 401"));
            Assert.That(e.ErrorCode, Is.Empty);
        }

        [Test]
        public void エラーにトークンとURLを入れない()
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(HttpStatusCode.Unauthorized));
            var e = Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!;
            Assert.That(e.ToString(), Does.Not.Contain(TestConnections.TokenA).And.Not.Contain("example.cybozu.com"));
        }

        // ---------------------------------------------------------------- 書き込み

        [Test]
        public async Task 作成はPOSTでアプリとレコードを送りidとrevisionを返す()
        {
            var result = await _connection.CreateAsync("Customer", NameRecord("新規"));

            var request = _fake.Requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Url, Is.EqualTo(FakeKintone.ApiUrl + "/record.json"));
            Assert.That(request.Header("X-HTTP-Method-Override"), Is.Null);
            Assert.That(request.Header("Content-Type"), Does.StartWith("application/json"));
            Assert.That(request.Json!.ToJsonString(), Is.EqualTo(JsonNode.Parse("""{"app":11,"record":{"Name":{"value":"新規"}}}""")!.ToJsonString()));
            Assert.That((result.Id, result.Revision), Is.EqualTo(("101", "1")));
        }

        [TestCase("""{"id":12,"revision":3}""", "12", "3")]
        [TestCase("""{"id":"12"}""", "12", "")]
        public async Task 作成の応答のidは文字でも数値でも読む(string body, string id, string revision)
        {
            _fake.Override(e => e.IsCreate, _ => FakeKintone.Status(HttpStatusCode.OK, body));
            var result = await _connection.CreateAsync("Customer", NameRecord("a"));
            Assert.That((result.Id, result.Revision), Is.EqualTo((id, revision)));
        }

        [TestCase("{}")]
        [TestCase("""{"id":"abc"}""")]
        [TestCase("""{"id":null}""")]
        [TestCase("not json")]
        [TestCase("[]")]
        public void 作成の応答にidが無ければ結果不明(string body)
        {
            _fake.Override(e => e.IsCreate, _ => FakeKintone.Status(HttpStatusCode.OK, body));
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown));
            Assert.That(e.Message, Does.Contain("did not return its id"));
        }

        [Test]
        public async Task 更新はPUTでidとrevisionとレコードを送る()
        {
            Assert.That(await _connection.UpdateAsync("Customer", "5", null, "3", NameRecord("x")), Is.True);
            var request = _fake.Requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(request.Url, Is.EqualTo(FakeKintone.ApiUrl + "/record.json"));
            Assert.That(request.Body, Is.EqualTo("""{"app":11,"id":"5","revision":"3","record":{"Name":{"value":"x"}}}"""));
        }

        [Test]
        public async Task 版なしの更新はrevisionを送らずupdateKeyの更新はidを送らない()
        {
            await _connection.UpdateAsync("Customer", "5", null, null, NameRecord("x"));
            await _connection.UpdateAsync("Customer", null, ("Code", JsonValue.Create("C-1")), "", NameRecord("y"));
            await _connection.UpdateAsync("Customer", null, ("Code", JsonValue.Create("C-2")), "4", new JsonObject());
            Assert.That(_fake.Requests.Select(e => e.Body), Is.EqualTo(new[]
            {
                """{"app":11,"id":"5","record":{"Name":{"value":"x"}}}""",
                """{"app":11,"updateKey":{"field":"Code","value":"C-1"},"record":{"Name":{"value":"y"}}}""",
                """{"app":11,"updateKey":{"field":"Code","value":"C-2"},"revision":"4","record":{}}""",
            }));
        }

        [TestCase(HttpStatusCode.NotFound, "GAIA_RE01")]
        [TestCase((HttpStatusCode)520, "GAIA_RE01")]
        [TestCase(HttpStatusCode.Conflict, "GAIA_CO02")]
        [TestCase((HttpStatusCode)520, "GAIA_CO02")]
        public async Task 更新と削除は対象が無い版が違うならfalse(HttpStatusCode status, string code)
        {
            _fake.Override(e => e.IsWrite, _ => FakeKintone.Error(status, code, "x"), 2);
            Assert.That(await _connection.UpdateAsync("Customer", "5", null, "3", NameRecord("x")), Is.False);
            Assert.That(await _connection.DeleteAsync("Customer", "5", "3"), Is.False);
            Assert.That(_fake.Requests, Has.Count.EqualTo(2));
        }

        [TestCase(HttpStatusCode.NotFound, "GAIA_AP01", KintoneErrorKind.NotFound)]
        [TestCase(HttpStatusCode.Forbidden, "GAIA_NO01", KintoneErrorKind.Authentication)]
        [TestCase(HttpStatusCode.Conflict, "GAIA_XX01", KintoneErrorKind.InvalidRequest)]
        [TestCase(HttpStatusCode.BadRequest, "CB_VA01", KintoneErrorKind.InvalidRequest)]
        public void 更新と削除のほかのエラーはfalseにしない(HttpStatusCode status, string code, KintoneErrorKind kind)
        {
            _fake.Override(e => e.IsWrite, _ => FakeKintone.Error(status, code, "x"), 2);
            Assert.That(Assert.ThrowsAsync<KintoneException>(() => _connection.UpdateAsync("Customer", "5", null, null, NameRecord("x")))!.Kind, Is.EqualTo(kind));
            Assert.That(Assert.ThrowsAsync<KintoneException>(() => _connection.DeleteAsync("Customer", "5", null))!.Kind, Is.EqualTo(kind));
        }

        [Test]
        public async Task 削除はDELETEでidsとrevisionsをURLに書く()
        {
            Assert.That(await _connection.DeleteAsync("Customer", "5", "3"), Is.True);
            Assert.That(await _connection.DeleteAsync("Region", "6", null), Is.True);
            var requests = _fake.Requests;
            Assert.That(requests.Select(e => e.Method), Is.EqualTo(new[] { HttpMethod.Delete, HttpMethod.Delete }));
            Assert.That(requests[0].Url, Is.EqualTo(FakeKintone.ApiUrl + "/records.json?app=11&ids%5B0%5D=5&revisions%5B0%5D=3"));
            Assert.That(requests[1].Url, Is.EqualTo(FakeKintone.ApiUrl + "/records.json?app=12&ids%5B0%5D=6"));
            Assert.That(requests[0].Body, Is.Empty);
        }

        [Test]
        public void 書き込みは通信の失敗をやり直さず結果不明()
        {
            _fake.Throw(_ => true, () => new HttpRequestException("reset at example.cybozu.com"), 10);
            var create = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            var update = Assert.ThrowsAsync<KintoneException>(() => _connection.UpdateAsync("Customer", "5", null, null, NameRecord("a")))!;
            var delete = Assert.ThrowsAsync<KintoneException>(() => _connection.DeleteAsync("Customer", "5", null))!;
            foreach (var e in new[] { create, update, delete })
            {
                Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown));
                Assert.That(e.Message, Does.Contain("unknown").And.Not.Contain("example.cybozu.com"));
            }
            Assert.That(_fake.Requests, Has.Count.EqualTo(3));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public void 書き込みのタイムアウトも結果不明でホスト名を入れない()
        {
            _fake.Throw(_ => true, () => new TaskCanceledException("The request to https://example.cybozu.com/ was canceled due to the configured HttpClient.Timeout"));
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.DeleteAsync("Customer", "5", null))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown));
            Assert.That(e.Message, Does.Not.Contain("example.cybozu.com"));
            Assert.That(e.InnerException, Is.InstanceOf<TaskCanceledException>());
        }

        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.GatewayTimeout)]
        [TestCase((HttpStatusCode)520)]
        public void 書き込みでエラーコードの無い5xxは結果不明(HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(status, "<html>bad gateway</html>"), 10);
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown));
            Assert.That(e.StatusCode, Is.EqualTo(status));
            Assert.That(e.Message, Does.Contain("unknown"));
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void 書き込みでエラーコードのある520は処理されなかったと確定したエラー()
        {
            _fake.Override(_ => true, _ => FakeKintone.Error((HttpStatusCode)520, "CB_IL02", "不正なリクエストです。"));
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(e.ErrorCode, Is.EqualTo("CB_IL02"));
            Assert.That(e.Message, Is.EqualTo("kintone: 不正なリクエストです。 (error CB_IL02)"));
        }

        [Test]
        public void 書き込みは429をやり直さない()
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(HttpStatusCode.TooManyRequests), 10);
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.ServiceUnavailable));
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [TestCase("POST", HttpStatusCode.Created)]
        [TestCase("POST", HttpStatusCode.Accepted)]
        [TestCase("PUT", HttpStatusCode.NoContent)]
        [TestCase("DELETE", HttpStatusCode.Accepted)]
        [TestCase("DELETE", HttpStatusCode.NoContent)]
        public void 書き込みで200以外の2xxは結果不明(string method, HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(status, """{"id":"1","revision":"1"}"""));
            Task action = method switch
            {
                "POST" => _connection.CreateAsync("Customer", NameRecord("a")),
                "PUT" => _connection.UpdateAsync("Customer", "5", null, null, NameRecord("a")),
                _ => _connection.DeleteAsync("Customer", "5", null),
            };
            var e = Assert.ThrowsAsync<KintoneException>(async () => await action)!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown));
        }

        // ---------------------------------------------------------------- リダイレクト

        static HttpResponseMessage Redirect(HttpStatusCode status)
        {
            var response = FakeKintone.Status(status, """{"records":[],"id":"1","revision":"1"}""");
            response.Headers.Location = new Uri("https://other.example.net/k/v1/records.json");
            return response;
        }

        static IEnumerable<HttpStatusCode> RedirectStatuses => new[]
        {
            HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect,
        };

        static void AssertRedirectError(KintoneException e, HttpStatusCode status)
        {
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(e.StatusCode, Is.EqualTo(status));
            Assert.That(e.Message, Does.Contain("redirect").And.Contain("Host"));
            Assert.That(e.Message, Does.Not.Contain("other.example.net"));
        }

        [TestCaseSource(nameof(RedirectStatuses))]
        public void 読み出しのリダイレクトには従わずやり直さない(HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => Redirect(status), 10);
            AssertRedirectError(Assert.ThrowsAsync<KintoneException>(() => ReadAsync())!, status);
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [TestCaseSource(nameof(RedirectStatuses))]
        public void 書き込みのリダイレクトは成功にも結果不明にもしない(HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => Redirect(status), 10);
            AssertRedirectError(Assert.ThrowsAsync<KintoneException>(() => _connection.CreateAsync("Customer", NameRecord("a")))!, status);
            AssertRedirectError(Assert.ThrowsAsync<KintoneException>(() => _connection.UpdateAsync("Customer", "5", null, null, NameRecord("a")))!, status);
            AssertRedirectError(Assert.ThrowsAsync<KintoneException>(() => _connection.DeleteAsync("Customer", "5", null))!, status);
            Assert.That(_fake.Requests, Has.Count.EqualTo(3));
        }

        [Test]
        public void 既定のHTTPはリダイレクトに従わない()
        {
            //実際の通信 (既定の HttpClient) で、リダイレクトを返すサーバーと転送先のサーバーを立てる
            using var origin = new LoopbackServer();
            using var other = new LoopbackServer();
            origin.Respond = c =>
            {
                c.Response.StatusCode = 307;
                c.Response.RedirectLocation = other.Url + c.Request.RawUrl!.TrimStart('/');
            };
            other.Respond = c => c.Response.StatusCode = 200;
            var settings = TestConnections.CreateSettings(e => e.Host = origin.Url.TrimEnd('/'));
            var connection = new KintoneConnection(settings);

            var e = Assert.ThrowsAsync<KintoneException>(() => connection.CreateAsync("Customer", NameRecord("secret")))!;

            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(origin.Requests, Is.EqualTo(new[] { "POST /k/v1/record.json" }));
            Assert.That(other.Requests, Is.Empty);
        }

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task 定義はアプリごとに1回だけ読み読み直しもできる()
        {
            var tasks = Enumerable.Range(0, 5).Select(_ => _connection.GetTableDefinitionAsync("Customer")).ToList();
            await Task.WhenAll(tasks);
            Assert.That(tasks.Select(e => e.Result).Distinct().Count(), Is.EqualTo(1));
            await _connection.GetTableDefinitionAsync("Region");
            await _connection.GetTableDefinitionAsync("Customer");
            Assert.That(_fake.Requests.Select(e => e.Url), Is.EqualTo(new[]
            {
                FakeKintone.ApiUrl + "/app/form/fields.json?app=11",
                FakeKintone.ApiUrl + "/app/form/fields.json?app=12",
            }));
            Assert.That(_fake.Requests.All(e => e.Method == HttpMethod.Get), Is.True);

            var all = await _connection.GetTableDefinitionsAsync();
            Assert.That(all.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            Assert.That(_fake.Requests, Has.Count.EqualTo(4));

            _fake.ClearRequests();
            var reloaded = await _connection.ReloadTableDefinitionsAsync();
            Assert.That(reloaded.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            Assert.That(_fake.Requests, Has.Count.EqualTo(4));
            Assert.That(await _connection.GetTableDefinitionAsync("Customer"), Is.SameAs(reloaded[0]));
        }

        [Test]
        public void 設定に無い表はNotFoundで要求を送らない()
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => _connection.GetTableDefinitionAsync("Nothing"))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotFound));
            Assert.That(e.Message, Does.Contain("'Nothing' is not in Apps"));
            Assert.ThrowsAsync<KintoneException>(() => _connection.ReadRecordsAsync("Nothing", Array.Empty<string>(), "limit 1", false));
            Assert.That(_fake.Requests, Is.Empty);
        }

        [Test]
        public void 読めなかった定義は持たずに次に読み直す()
        {
            _fake.Override(e => e.IsForm, _ => FakeKintone.Error(HttpStatusCode.Forbidden, "GAIA_NO01", "権限がありません。"));
            Assert.ThrowsAsync<KintoneException>(() => _connection.GetTableDefinitionAsync("Customer"));
            Assert.DoesNotThrowAsync(() => _connection.GetTableDefinitionAsync("Customer"));
            Assert.That(_fake.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void FromDataSourcesはkintoneのデータソースだけを接続にし同じ設定には同じ接続()
        {
            DataSource Kintone(string token) => new() { Name = "KT", ConnectionString = $"Provider=Kintone;Host=https://example.cybozu.com;Apps=Customer:11;ApiTokens={token}" };
            var dataSources = new[]
            {
                new DataSource { Name = "Main", DataSourceType = DataSourceType.SQLite, ConnectionString = "Data Source=a.db" },
                Kintone("conn-test-token-1"),
            };
            var first = KintoneConnection.FromDataSources(dataSources);
            var second = KintoneConnection.FromDataSources(dataSources);
            Assert.That(first.Select(e => e.Settings.Name), Is.EqualTo(new[] { "KT" }));
            Assert.That(second[0], Is.SameAs(first[0]));
            //トークンを変えたら別の接続
            var changed = KintoneConnection.FromDataSources(new[] { Kintone("conn-test-token-2") });
            Assert.That(changed[0], Is.Not.SameAs(first[0]));
        }

        [Test]
        public void FromDataSourcesは設定の誤りを例外にする()
        {
            var dataSources = new[] { new DataSource { Name = "KT", ConnectionString = "Provider=Kintone;Host=https://example.cybozu.com;ApiTokens=t" } };
            Assert.Throws<KintoneException>(() => KintoneConnection.FromDataSources(dataSources));
        }

        /// <summary>localhost の空いているポートで待つ HTTP サーバー (受けた要求を "メソッド パス" で記録する)。</summary>
        internal sealed class LoopbackServer : IDisposable
        {
            readonly HttpListener _listener = new();

            public string Url { get; }
            public List<string> Requests { get; } = new();
            public Action<HttpListenerContext> Respond { get; set; } = c => c.Response.StatusCode = 404;

            public LoopbackServer()
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = $"http://localhost:{port}/";
                _listener.Prefixes.Add(Url);
                _listener.Start();
                _ = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context;
                        try { context = await _listener.GetContextAsync(); }
                        catch { return; }
                        lock (Requests) Requests.Add(context.Request.HttpMethod + " " + context.Request.RawUrl);
                        Respond(context);
                        context.Response.Close();
                    }
                });
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
