using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using System.Net;
using System.Text;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    public class KintoneAccountTest
    {
        const string LoginName = "sato@example.com";
        const string Password = "fake-pass-123";

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

        static string PasswordHeader(string name, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password));

        [Test]
        public async Task 利用者のパスワード認証でアプリの一覧を1件読む()
        {
            var result = await _connection.VerifyAccountAsync(LoginName, Password);

            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.Succeeded));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.LoginName, Is.EqualTo(LoginName));
            Assert.That(result.Message, Is.Empty);
            var request = _fake.Requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Url, Is.EqualTo(FakeKintone.ApiUrl + "/apps.json?limit=1"));
            Assert.That(request.Header("X-Cybozu-Authorization"), Is.EqualTo(PasswordHeader(LoginName, Password)));
            //連携用の API トークンは付けない
            Assert.That(request.Header("X-Cybozu-API-Token"), Is.Null);
        }

        [Test]
        public async Task 接続がパスワード認証でも利用者のログイン名とパスワードで送る()
        {
            var connection = TestConnections.Create(_fake, null, e =>
            {
                e.ApiTokens.Clear();
                e.User = "integration-user";
                e.Password = "integration-pass";
            });
            await connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(_fake.Requests.Single().Header("X-Cybozu-Authorization"), Is.EqualTo(PasswordHeader(LoginName, Password)));
        }

        [Test]
        public async Task 確認のあとも連携用の要求は接続の認証で送る()
        {
            await _connection.VerifyAccountAsync(LoginName, Password);
            await _connection.ReadRecordsAsync("Customer", Array.Empty<string>(), "limit 1", false);
            var read = _fake.Requests[1];
            Assert.That(read.Header("X-Cybozu-API-Token"), Is.EqualTo($"{TestConnections.TokenA},{TestConnections.TokenB}"));
            Assert.That(read.Header("X-Cybozu-Authorization"), Is.Null);
        }

        [Test]
        public async Task 拒否されたらInvalidCredentials()
        {
            _fake.Override(_ => true, _ => FakeKintone.Error(HttpStatusCode.Unauthorized, "CB_WA01", "ユーザーのパスワード認証に失敗しました。"));
            var result = await _connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.InvalidCredentials));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.LoginName, Is.Empty);
            Assert.That(result.Message, Is.Not.Empty.And.Not.Contain(Password));
        }

        [TestCase("", Password)]
        [TestCase("sato:admin", Password)]
        [TestCase(LoginName, "")]
        public async Task 空やコロンを含むログイン名は送らずにInvalidCredentials(string name, string password)
        {
            var result = await _connection.VerifyAccountAsync(name, password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.InvalidCredentials));
            Assert.That(_fake.Requests, Is.Empty);
        }

        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.TooManyRequests)]
        public async Task 一時的な失敗でもやり直さずFailed(HttpStatusCode status)
        {
            _fake.Override(_ => true, _ => FakeKintone.Status(status), 10);
            var result = await _connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.Failed));
            Assert.That(result.LoginName, Is.Empty);
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public async Task 権限が無いなど401以外の拒否はFailed()
        {
            _fake.Override(_ => true, _ => FakeKintone.Error(HttpStatusCode.Forbidden, "CB_NO02", "権限がありません。"));
            var result = await _connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.Failed));
            Assert.That(result.Message, Does.Contain("CB_NO02"));
        }

        [Test]
        public async Task 届かなければFailedでメッセージにホスト名とパスワードを入れない()
        {
            _fake.Throw(_ => true, () => new HttpRequestException($"No such host is known. (example.cybozu.com) {Password}"), 10);
            var result = await _connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.Failed));
            Assert.That(result.Message, Does.Not.Contain("example.cybozu.com").And.Not.Contain(Password));
            Assert.That(_fake.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task リダイレクトは成功にしない()
        {
            _fake.Override(_ => true, _ =>
            {
                var response = FakeKintone.Status(HttpStatusCode.Found, """{"apps":[]}""");
                response.Headers.Location = new Uri("https://other.example.net/login");
                return response;
            });
            var result = await _connection.VerifyAccountAsync(LoginName, Password);
            Assert.That(result.Status, Is.EqualTo(KintoneAccountStatus.Failed));
            Assert.That(result.Message, Does.Not.Contain("other.example.net"));
        }

        [Test]
        public void 結果の文字列にパスワードを入れない()
        {
            var credentials = new KintoneConnection.AccountCredentials(LoginName, Password);
            Assert.That(credentials.ToString(), Is.EqualTo(LoginName));
        }
    }
}
