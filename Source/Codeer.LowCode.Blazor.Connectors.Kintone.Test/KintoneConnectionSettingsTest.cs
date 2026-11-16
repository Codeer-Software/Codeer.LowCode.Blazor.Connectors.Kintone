using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.SystemSettings;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    public class KintoneConnectionSettingsTest
    {
        static DataSource Source(string connectionString) => new() { Name = "KT", ConnectionString = connectionString };

        [Test]
        public void 接続文字列を読む()
        {
            var settings = KintoneConnectionSettings.FromDataSource(Source(
                "Provider=kintone;Host=https://example.cybozu.com/;Apps=Customer:12, 受注 : 13 ;ApiTokens= tokA , tokB ;GuestSpaceId=7;MaxRows=500;TimeoutSeconds=30"))!;
            Assert.That(settings.Name, Is.EqualTo("KT"));
            Assert.That(settings.Host, Is.EqualTo("https://example.cybozu.com"));
            Assert.That(settings.Apps.Select(e => (e.Name, e.AppId)), Is.EqualTo(new[] { ("Customer", 12L), ("受注", 13L) }));
            Assert.That(settings.ApiTokens, Is.EqualTo(new[] { "tokA", "tokB" }));
            Assert.That(settings.UsesPassword, Is.False);
            Assert.That(settings.GuestSpaceId, Is.EqualTo(7));
            Assert.That(settings.MaxRows, Is.EqualTo(500));
            Assert.That(settings.TimeoutSeconds, Is.EqualTo(30));
            Assert.That(settings.ApiUrl, Is.EqualTo("https://example.cybozu.com/k/guest/7/v1"));
            Assert.DoesNotThrow(() => settings.Validate());
        }

        [Test]
        public void パスワード認証とゲストスペースなしのURL()
        {
            var settings = KintoneConnectionSettings.FromDataSource(Source("Provider=Kintone;Host=https://example.cybozu.com;Apps=A:1;User=api-user;Password='p;w=d'"))!;
            Assert.That(settings.UsesPassword, Is.True);
            Assert.That(settings.User, Is.EqualTo("api-user"));
            Assert.That(settings.Password, Is.EqualTo("p;w=d"));
            Assert.That(settings.ApiTokens, Is.Empty);
            Assert.That(settings.ApiUrl, Is.EqualTo("https://example.cybozu.com/k/v1"));
            Assert.DoesNotThrow(() => settings.Validate());
        }

        [Test]
        public void 省略と不正な数値は既定値()
        {
            var settings = KintoneConnectionSettings.FromDataSource(Source(
                "Provider=Kintone;Host=https://example.cybozu.com;Apps=A:1;ApiTokens=t;MaxRows=abc;TimeoutSeconds=-1"))!;
            Assert.That(settings.MaxRows, Is.EqualTo(10000));
            Assert.That(settings.TimeoutSeconds, Is.EqualTo(100));
            Assert.That(settings.GuestSpaceId, Is.Null);
        }

        [TestCase("Data Source=a.db")]
        [TestCase("Provider=FileMaker;Host=https://example.cybozu.com")]
        [TestCase("")]
        [TestCase("Provider=Kintone;Host='unterminated")]
        public void Kintone用でなければnull(string connectionString)
            => Assert.That(KintoneConnectionSettings.FromDataSource(Source(connectionString)), Is.Null);

        [Test]
        public void 正しい設定は通る()
        {
            Assert.DoesNotThrow(() => TestConnections.CreateSettings().Validate());
            Assert.DoesNotThrow(() => TestConnections.CreateSettings(e => e.Host = "http://localhost:5100").Validate());
            Assert.DoesNotThrow(() => TestConnections.CreateSettings(e => e.Host = "http://127.0.0.1:5100").Validate());
            Assert.DoesNotThrow(() => TestConnections.CreateSettings(e => e.ApiTokens = Enumerable.Range(0, 9).Select(i => "t" + i).ToList()).Validate());
        }

        [TestCase("Host=https://example.cybozu.com;ApiTokens=t", "Apps is required")]
        [TestCase("Apps=A:1;ApiTokens=t", "Host is required")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1", "ApiTokens (or User / Password) is required")]
        [TestCase("Host=https://example.cybozu.com;Apps=A;ApiTokens=t", "TableName:AppId")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:x;ApiTokens=t", "TableName:AppId")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:0;ApiTokens=t", "TableName:AppId")]
        [TestCase("Host=https://example.cybozu.com;Apps=a.b:1;ApiTokens=t", "cannot contain")]
        [TestCase("Host=https://example.cybozu.com;Apps=:1;ApiTokens=t", "TableName:AppId")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1,A:2;ApiTokens=t", "same table name")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1,B:1;ApiTokens=t", "same app id")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;ApiTokens=t;User=u;Password=p", "not both")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;User=u", "Password is required")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;User=u:x;Password=p", "cannot contain ':'")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;ApiTokens=t;GuestSpaceId=x", "GuestSpaceId")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;ApiTokens=t;GuestSpaceId=0", "GuestSpaceId")]
        [TestCase("Host=https://example.cybozu.com;Apps=A:1;ApiTokens=1,2,3,4,5,6,7,8,9,10", "up to 9")]
        public void 設定の誤り(string connectionString, string message)
        {
            var settings = KintoneConnectionSettings.FromDataSource(Source("Provider=Kintone;" + connectionString))!;
            var e = Assert.Throws<KintoneException>(() => settings.Validate())!;
            Assert.That(e.Message, Does.Contain(message));
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
        }

        [TestCase("http://example.cybozu.com")]
        [TestCase("ftp://example.cybozu.com")]
        [TestCase("example.cybozu.com")]
        [TestCase("https://example.cybozu.com?x=1")]
        [TestCase("https://example.cybozu.com#a")]
        public void Hostはhttpsだけ(string host)
        {
            var settings = TestConnections.CreateSettings(e => e.Host = host);
            Assert.Throws<KintoneException>(() => settings.Validate());
        }

        [Test]
        public void 設定の誤りのメッセージにトークンとパスワードを入れない()
        {
            var settings = KintoneConnectionSettings.FromDataSource(Source("Provider=Kintone;Host=http://example.cybozu.com;Apps=A:1;ApiTokens=secret-token-value"))!;
            var e = Assert.Throws<KintoneException>(() => settings.Validate())!;
            Assert.That(e.Message, Does.Not.Contain("secret-token-value"));
            settings = KintoneConnectionSettings.FromDataSource(Source("Provider=Kintone;Host=https://example.cybozu.com;Apps=A:1;User=u;Password=secret-password;ApiTokens=t"))!;
            e = Assert.Throws<KintoneException>(() => settings.Validate())!;
            Assert.That(e.Message, Does.Not.Contain("secret-password"));
        }

        [Test]
        public void 接続は設定を確かめる()
            => Assert.Throws<KintoneException>(() => new KintoneConnection(TestConnections.CreateSettings(e => e.Host = "http://example.cybozu.com")));
    }
}
