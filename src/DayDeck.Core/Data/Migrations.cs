namespace DayDeck.Core.Data;

/// <summary>
/// 스키마 마이그레이션 목록. 적용 여부는 <c>PRAGMA user_version</c>으로 추적한다.
/// 이미 배포된 마이그레이션은 절대 수정하지 말고 새 버전을 추가한다.
/// </summary>
internal static class Migrations
{
    public static IReadOnlyList<(int Version, string Sql)> All { get; } =
    [
        (1, V1),
        (2, V2),
        (3, V3),
        (4, V4),
        (5, V5),
    ];

    public static int Latest => All[^1].Version;

    // 설계 문서 §4.1
    private const string V1 = """
        CREATE TABLE lists (
          id          INTEGER PRIMARY KEY,
          name        TEXT NOT NULL,
          color       TEXT,
          sort_order  INTEGER NOT NULL DEFAULT 0,
          is_archived INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE items (
          id            INTEGER PRIMARY KEY,
          kind          INTEGER NOT NULL,
          list_id       INTEGER REFERENCES lists(id) ON DELETE SET NULL,
          parent_id     INTEGER REFERENCES items(id) ON DELETE CASCADE,
          title         TEXT NOT NULL,
          notes         TEXT,
          priority      INTEGER NOT NULL DEFAULT 0,
          is_all_day    INTEGER NOT NULL DEFAULT 0,
          start_at      TEXT,
          end_at        TEXT,
          due_at        TEXT,
          rrule         TEXT,
          status        INTEGER NOT NULL DEFAULT 0,
          completed_at  TEXT,
          location      TEXT,
          color         TEXT,
          sort_order    INTEGER NOT NULL DEFAULT 0,
          created_at    TEXT NOT NULL,
          updated_at    TEXT NOT NULL,
          deleted_at    TEXT
        );
        CREATE INDEX ix_items_start  ON items(start_at) WHERE deleted_at IS NULL;
        CREATE INDEX ix_items_due    ON items(due_at)   WHERE deleted_at IS NULL;
        CREATE INDEX ix_items_status ON items(status, list_id);
        CREATE INDEX ix_items_parent ON items(parent_id);

        CREATE TABLE occurrences (
          item_id        INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
          original_start TEXT NOT NULL,
          status         INTEGER,
          override_json  TEXT,
          PRIMARY KEY (item_id, original_start)
        );

        CREATE TABLE reminders (
          id             INTEGER PRIMARY KEY,
          item_id        INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
          offset_minutes INTEGER,
          absolute_at    TEXT,
          next_fire_at   TEXT,
          snoozed_until  TEXT,
          last_fired_at  TEXT,
          is_persistent  INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_reminders_next ON reminders(next_fire_at);
        CREATE INDEX ix_reminders_item ON reminders(item_id);

        CREATE TABLE tags (
          id    INTEGER PRIMARY KEY,
          name  TEXT NOT NULL UNIQUE,
          color TEXT
        );

        CREATE TABLE item_tags (
          item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
          tag_id  INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
          PRIMARY KEY (item_id, tag_id)
        );

        -- 메모 파일 목록 캐시 (실제 내용은 Notes 폴더의 파일)
        CREATE TABLE notes_index (
          path        TEXT PRIMARY KEY,
          title       TEXT NOT NULL,
          preview     TEXT,
          modified_at TEXT NOT NULL,
          is_pinned   INTEGER NOT NULL DEFAULT 0
        );

        -- 전문 검색. items와 동기화는 트리거로 한다.
        CREATE VIRTUAL TABLE items_fts USING fts5(title, notes, content='items', content_rowid='id');

        CREATE TRIGGER items_fts_ai AFTER INSERT ON items BEGIN
          INSERT INTO items_fts(rowid, title, notes) VALUES (new.id, new.title, new.notes);
        END;
        CREATE TRIGGER items_fts_ad AFTER DELETE ON items BEGIN
          INSERT INTO items_fts(items_fts, rowid, title, notes) VALUES ('delete', old.id, old.title, old.notes);
        END;
        CREATE TRIGGER items_fts_au AFTER UPDATE OF title, notes ON items BEGIN
          INSERT INTO items_fts(items_fts, rowid, title, notes) VALUES ('delete', old.id, old.title, old.notes);
          INSERT INTO items_fts(rowid, title, notes) VALUES (new.id, new.title, new.notes);
        END;
        """;

    // M2: "오늘 할 일"에 직접 추가한 날짜, 처음 실행할 때 기본 목록
    private const string V2 = """
        ALTER TABLE items ADD COLUMN my_day TEXT;
        CREATE INDEX ix_items_my_day ON items(my_day) WHERE my_day IS NOT NULL;
        CREATE INDEX ix_items_completed ON items(completed_at) WHERE completed_at IS NOT NULL;

        INSERT INTO lists(name, color, sort_order)
        SELECT column1, column2, column3 FROM (VALUES ('개인', '#0F7B0F', 0), ('업무', '#2B6CD4', 1))
        WHERE NOT EXISTS (SELECT 1 FROM lists);
        """;

    // M3: 알림 상태 추적, 앱 상태(마지막 브리핑 날짜 등)
    //  target_at    : 마지막으로 계산한 알림 시각. 마감이 바뀌어 이 값이 바뀌면 알림을 다시 켠다.
    //  dismissed_for: 사용자가 확인한(또는 한 번 울린) target_at. 같은 시각으로 두 번 울리지 않는다.
    private const string V3 = """
        ALTER TABLE reminders ADD COLUMN target_at TEXT;
        ALTER TABLE reminders ADD COLUMN dismissed_for TEXT;

        CREATE TABLE app_state (
          key   TEXT PRIMARY KEY,
          value TEXT
        );
        """;

    // M8: 습관과 집중(뽀모도로) 기록
    //  days_mask: 해야 하는 요일 (일=1, 월=2, … 토=64). 127이면 매일.
    private const string V4 = """
        CREATE TABLE habits (
          id          INTEGER PRIMARY KEY,
          name        TEXT NOT NULL,
          color       TEXT,
          days_mask   INTEGER NOT NULL DEFAULT 127,
          sort_order  INTEGER NOT NULL DEFAULT 0,
          created_at  TEXT NOT NULL,
          archived_at TEXT
        );

        CREATE TABLE habit_checks (
          habit_id INTEGER NOT NULL REFERENCES habits(id) ON DELETE CASCADE,
          day      TEXT NOT NULL,
          PRIMARY KEY (habit_id, day)
        );

        CREATE TABLE focus_sessions (
          id         INTEGER PRIMARY KEY,
          item_id    INTEGER REFERENCES items(id) ON DELETE SET NULL,
          started_at TEXT NOT NULL,
          minutes    INTEGER NOT NULL
        );
        CREATE INDEX ix_focus_started ON focus_sessions(started_at);
        """;

    // 반복 할 일: 완료하면 다음 회차를 새 할 일로 만들고, 완료한 항목에 그 id를 남긴다 (완료 취소 때 되돌리기용).
    private const string V5 = """
        ALTER TABLE items ADD COLUMN recur_next_id INTEGER;
        """;
}
