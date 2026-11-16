using LowCodeApp.Server.Extras;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Codeer.LowCode.Blazor.Utils;
using Microsoft.AspNetCore.Authorization;
using LowCodeApp.Client;
using LowCodeApp.Server.Infrastructure;
using Codeer.LowCode.Blazor.Extras.Server.AuditLog;
using Codeer.LowCode.Blazor.Extras.Server.Auth;
using Codeer.LowCode.Blazor.Extras.Server.Mail;
using Microsoft.Extensions.Caching.Distributed;
using Codeer.LowCode.Blazor.Connectors.Kintone.Server;

namespace LowCodeApp.Server.Controllers
{
    [ApiController, AutoValidateAntiforgeryToken]
    [Route("api/account")]
    public class AccountController : ControllerBase
    {
        const string LoginPage = "/login.html";

        readonly DataService _dataService;
        readonly ExternalLoginService _externalLogins;
        readonly IDistributedCache _cache;
        readonly AuditContext _audit;

        public AccountController(DataService dataService, ExternalLoginService externalLogins, IDistributedCache cache, AuditContext audit)
        {
            _dataService = dataService;
            _externalLogins = externalLogins;
            _cache = cache;
            _audit = audit;
        }

        [HttpGet("current_user")]
        public StringWrapper GetCurrentUser()
            => new(LoginPrincipal.GetUserId(User));

        //antiforgery トークンの Cookie を発行する (login.html とアプリの起動時に取る)
        [HttpGet("antiforgery")]
        public IActionResult Antiforgery()
        {
            CookieAuthentication.AppendAntiforgeryTokenCookie(HttpContext);
            return NoContent();
        }

        //ログイン画面に出すもの (ID/パスワードのフォームの有無・外部 IdP のボタン)。見た目は login.html (Web) と Login.razor (MAUI) で変える
        [HttpGet("login_options")]
        public object LoginOptions()
            => new
            {
                Password = SystemConfig.Instance.AllowPasswordLogin,
                Providers = _externalLogins.Options,
                Kintone = SystemConfig.Instance.KintoneLogin.IsEnabled,
            };

        //kintone のアカウントでログイン。kintone がログイン名とパスワードを確かめ、そのログイン名でユーザーの行を引いて Cookie を発行する。
        //パスワードはどこにも残さない。ログインのあとのデータの読み書きは連携用の認証 (API トークン) のまま
        [HttpPost("login_kintone"), Audit(AuditCategory.Authentication)]
        public async Task<IActionResult> LoginKintone(LoginInfo? loginInfo)
        {
            if (loginInfo == null) throw new ArgumentException(nameof(loginInfo));
            var settings = SystemConfig.Instance.KintoneLogin;
            var connection = settings.IsEnabled
                ? KintoneConnection.FromDataSources(SystemConfig.Instance.DataSources).FirstOrDefault(e => e.Settings.Name == settings.DataSourceName)
                : null;
            if (connection == null) return NotFound();
            _audit.Event.Detail = $"Provider={KintoneProvider}; LoginName={loginInfo.Id}";

            //失敗が続いたログイン名はロックする (ID/パスワードのログインと同じ設定。鍵は分ける)
            var lockout = new LoginLockout(SystemConfig.Instance.LoginLockout, _cache);
            var lockName = KintoneProvider + ":" + loginInfo.Id;
            if (!await lockout.TryStartAttemptAsync(lockName))
            {
                _audit.Deny($"LoginName={loginInfo.Id}; locked");
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var verified = await connection.VerifyAccountAsync(loginInfo.Id ?? string.Empty, loginInfo.Password ?? string.Empty);
            if (verified.Status == KintoneAccountStatus.InvalidCredentials)
            {
                _audit.Deny($"LoginName={loginInfo.Id}; {verified.Message}");
                return Unauthorized();
            }
            if (!verified.Succeeded)
            {
                //kintone に届かないなど、アカウントの誤りではない失敗は数えない
                await lockout.CancelAttemptAsync(lockName);
                _audit.Event.Detail += $"; {verified.Message}";
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var accounts = LoginAccountStore.Create(_dataService.Design.DesignData, _dataService.DbAccess);
            var account = accounts == null ? null : await accounts.FindByExternalLoginNameAsync(verified.LoginName);
            if (account == null)
            {
                await lockout.CancelAttemptAsync(lockName);
                _audit.Deny($"LoginName={loginInfo.Id}; KintoneLoginName={verified.LoginName}; user_not_registered");
                return Unauthorized();
            }

            await lockout.ResetAsync(lockName);
            _audit.Event.UserId = account.UserId;
            _audit.Event.Detail += $"; KintoneLoginName={verified.LoginName}";
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                LoginPrincipal.Create(account.UserId, account.DisplayName, KintoneProvider, account.SessionStamp),
                new AuthenticationProperties { IsPersistent = loginInfo.IsPersistent });
            return Ok();
        }

        //Cookie に入れるログインの方法の名前 (外部 IdP の名前と重ならないもの。ログアウトは Cookie を消すだけ)
        const string KintoneProvider = "Kintone";

        [HttpPost("login"), Audit(AuditCategory.Authentication)]
        public async Task<IActionResult> Login(LoginInfo? loginInfo)
        {
            if (loginInfo == null) throw new ArgumentException(nameof(loginInfo));
            if (!SystemConfig.Instance.AllowPasswordLogin) return NotFound();
            _audit.Event.Detail = $"LoginName={loginInfo.Id}";

            //ID / パスワードの照合・ロックアウト・二要素認証は PasswordLogin。結果を HTTP と監査ログに写し、通ったら Cookie を発行する
            var login = new PasswordLogin(_dataService.Design.DesignData, _dataService.DbAccess, _cache,
                SystemConfig.Instance.LoginLockout, SystemConfig.Instance.TotpLogin, SystemConfig.Instance.EmailOtpLogin,
                message => new MailDispatcher(SystemConfig.Instance.Mail, MailSenderTable.Create).SendAsync(SystemConfig.Instance.EmailOtpLogin.MailInfraName, message));
            var result = await login.VerifyAsync(loginInfo.Id, loginInfo.Password, loginInfo.TwoFactorCode);

            if (result.IsDenied) _audit.Deny($"LoginName={loginInfo.Id}; {result.Detail}");
            else if (result.Detail != null) _audit.Event.Detail += $"; {result.Detail}";

            switch (result.Status)
            {
                case PasswordLoginStatus.NotAvailable: return NotFound();
                case PasswordLoginStatus.Locked: return StatusCode(StatusCodes.Status429TooManyRequests);
                case PasswordLoginStatus.Unauthorized: return Unauthorized();
                case PasswordLoginStatus.TwoFactor: return Ok(result.Response);   //登録情報・コードの要求・不一致: サインインしない
            }

            //監査ログのユーザー Id は Cookie を発行するときだけ入れる (二要素認証待ちの行には入れない)
            var account = result.Account!;
            _audit.Event.UserId = account.UserId;

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                LoginPrincipal.Create(account.UserId, account.DisplayName, sessionStamp: account.SessionStamp),
                new AuthenticationProperties { IsPersistent = loginInfo.IsPersistent });

            return Ok(result.Response);
        }

        //外部 IdP でログイン。IdP が本人確認した後、ExternalLoginUserResolver がユーザー行に解決して Cookie を発行する。
        //persistent = ログイン状態を保持する。mobile = ネイティブアプリ (Cookie の代わりに使い捨てチケットを返す)
        [HttpGet("login/{provider}"), Audit(AuditCategory.Authentication)]
        public IActionResult ExternalLogin(string provider, string? returnUrl, bool mobile = false, bool persistent = false)
            => _externalLogins.Challenge(this, provider, returnUrl, mobile, persistent);

        //ネイティブアプリ: システムブラウザで受け取った使い捨てチケットを認証 Cookie に交換する
        [HttpPost("login_ticket"), Audit(AuditCategory.Authentication)]
        public async Task<IActionResult> LoginTicket(LoginTicket? ticket)
        {
            var redeemed = await _externalLogins.RedeemMobileTicketAsync(ticket?.Ticket);
            if (redeemed == null) return Unauthorized();
            _audit.Event.UserId = redeemed.Value.User.UserId;
            _audit.Event.Detail = $"Provider={redeemed.Value.Provider}";

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                LoginPrincipal.Create(redeemed.Value.User.UserId, redeemed.Value.User.UserName, redeemed.Value.Provider, redeemed.Value.User.SessionStamp),
                new AuthenticationProperties { IsPersistent = true });

            return Ok();
        }

        //外部 IdP でサインインしたセッションは IdP 側のログアウトにブラウザ遷移が要るので、Cookie を残したまま遷移先 URL を返す
        [Authorize]
        [HttpPost("logout"), Audit(AuditCategory.Authentication)]
        public async Task<IActionResult> Logout(bool mobile = false)
        {
            if (!mobile)
            {
                var provider = await _externalLogins.GetSignOutProviderAsync(User);
                if (provider != null) return Ok(new LogoutResult { Redirect = $"/api/account/logout/{provider}" });
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new LogoutResult());
        }

        //Cookie を破棄し IdP のセッションも終わらせてログイン画面へ戻る (GET: ブラウザ遷移)
        [HttpGet("logout/{provider}"), Audit(AuditCategory.Authentication)]
        public async Task<IActionResult> ExternalLogout(string provider)
        {
            if (Request.Headers["Sec-Fetch-Site"] == "cross-site") return Redirect("/");
            return await _externalLogins.SignOutAsync(this, provider, LoginPage);
        }
    }
}
