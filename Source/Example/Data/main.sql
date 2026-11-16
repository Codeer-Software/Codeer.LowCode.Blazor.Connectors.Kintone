-- データソース Main (SQLite) の表: ログインするユーザーと一時ファイル。kintone のデータはここに置かない
-- 初期ユーザー: admin / admin
-- taro は kintone のアカウント (モックのユーザー yamada.taro) でだけ入れるユーザー (パスワードを持たない。kintone_login_name が kintone のログイン名)
CREATE TABLE app_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name TEXT NOT NULL UNIQUE,
    name TEXT,
    email TEXT,
    kintone_login_name TEXT UNIQUE,
    hash TEXT NOT NULL,
    salt TEXT NOT NULL,
    session_stamp TEXT
);

CREATE TABLE temporary_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guid TEXT NOT NULL,
    created_date_time TEXT NULL
);

INSERT INTO app_users (id, user_name, name, email, kintone_login_name, hash, salt) VALUES
(1, 'admin', '管理者', NULL, NULL, 'pUNVPHEWYngAim7aUFjf6kWtIROxrpzJPprcipyPujw=', 'CPYn8K+ecURogSHP5KTZ04lEbiAjZoq9wvwk5aIAQQg='),
(2, 'taro', '山田 太郎', 'taro.yamada@example.com', 'yamada.taro', '', '');
