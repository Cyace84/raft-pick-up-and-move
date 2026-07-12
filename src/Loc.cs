using System.Collections.Generic;
using UnityEngine;

namespace PickUpMove
{
    // Tiny localisation table for the mod's on-screen strings (the "Move" hint + the pop-up
    // refusal notes). Logs stay English. The current language is read from the game's own I2
    // Localization (LocalizationManager.CurrentLanguageCode -> ISO code like "ru"/"de"/"pt-BR"),
    // matched on the 2-letter prefix, English fallback.
    //
    // T(key): returns the localised string for `key`; if `key` is unknown it is returned VERBATIM.
    // That pass-through is deliberate - refusal reasons travel over the wire as KEYS so host and
    // client each render in THEIR language, but a dynamic/untranslated reason (e.g. a composed
    // dependent-check message) simply shows as-is instead of blanking.
    internal static class Loc
    {
        private static string _code;        // cached 2-letter language code
        private static float _codeUntil;    // re-read CurrentLanguageCode after this (settings can change it)

        private static string Code()
        {
            if (_code != null && Time.realtimeSinceStartup < _codeUntil) return _code;
            string c = "en";
            try
            {
                var raw = I2.Loc.LocalizationManager.CurrentLanguageCode; // "en", "ru", "pt-BR", ...
                if (!string.IsNullOrEmpty(raw))
                {
                    int dash = raw.IndexOf('-');
                    c = (dash > 0 ? raw.Substring(0, dash) : raw).ToLowerInvariant();
                }
            }
            catch { c = "en"; }
            _code = c; _codeUntil = Time.realtimeSinceStartup + 1f;
            return c;
        }

        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (Table.TryGetValue(key, out var byLang))
            {
                if (byLang.TryGetValue(Code(), out var s)) return s;
                if (byLang.TryGetValue("en", out var en)) return en;
            }
            return key; // unknown key -> verbatim (dynamic reasons, safety)
        }

        // key -> (langCode -> text). "en" is the base + fallback for every key.
        private static readonly Dictionary<string, Dictionary<string, string>> Table = new Dictionary<string, Dictionary<string, string>>
        {
            ["move"] = new Dictionary<string, string> {
                ["en"]="Move", ["ru"]="Переместить", ["de"]="Verschieben", ["fr"]="Déplacer", ["sv"]="Flytta",
                ["it"]="Sposta", ["pt"]="Mover", ["zh"]="移动", ["ja"]="移動", ["ko"]="이동", ["pl"]="Przenieś" },

            ["busy"] = new Dictionary<string, string> {
                ["en"]="Finishing the previous move. Try again in a moment.",
                ["ru"]="Завершаю предыдущее перемещение. Попробуйте через секунду.",
                ["de"]="Beende die vorherige Verschiebung. Versuche es gleich erneut.",
                ["fr"]="Déplacement précédent en cours. Réessayez dans un instant.",
                ["sv"]="Avslutar den förra flytten. Försök igen om ett ögonblick.",
                ["it"]="Sto completando lo spostamento precedente. Riprova tra un istante.",
                ["pt"]="Concluindo o movimento anterior. Tente novamente em um instante.",
                ["zh"]="正在完成上一次移动，请稍后再试。",
                ["ja"]="前回の移動を完了中です。少し待ってからもう一度お試しください。",
                ["ko"]="이전 이동을 완료하는 중입니다. 잠시 후 다시 시도하세요.", ["pl"]="Kończę poprzednie przenoszenie. Spróbuj ponownie za chwilę." },

            ["plank"] = new Dictionary<string, string> {
                ["en"]="This plank is stretched between two points and can't be carried.",
                ["ru"]="Эта доска растянута между двумя точками — её нельзя переносить.",
                ["de"]="Diese Planke ist zwischen zwei Punkten gespannt und kann nicht getragen werden.",
                ["fr"]="Cette planche est tendue entre deux points et ne peut pas être déplacée.",
                ["sv"]="Den här plankan är spänd mellan två punkter och kan inte bäras.",
                ["it"]="Questa asse è tesa tra due punti e non può essere spostata.",
                ["pt"]="Esta tábua está esticada entre dois pontos e não pode ser carregada.",
                ["zh"]="这块木板横跨两点之间，无法搬动。",
                ["ja"]="この板は2点の間に渡されているため、運べません。",
                ["ko"]="이 널빤지는 두 지점 사이에 걸쳐 있어 옮길 수 없습니다.", ["pl"]="Ta deska jest rozciągnięta między dwoma punktami i nie można jej przenieść." },

            ["rope"] = new Dictionary<string, string> {
                ["en"]="Detach the rope before moving the zipline.",
                ["ru"]="Сначала снимите верёвку, потом двигайте зиплайн.",
                ["de"]="Löse das Seil, bevor du die Seilrutsche bewegst.",
                ["fr"]="Détachez la corde avant de déplacer la tyrolienne.",
                ["sv"]="Ta loss repet innan du flyttar linbanan.",
                ["it"]="Stacca la corda prima di spostare la teleferica.",
                ["pt"]="Solte a corda antes de mover a tirolesa.",
                ["zh"]="移动滑索前请先解开绳索。",
                ["ja"]="ジップラインを動かす前にロープを外してください。",
                ["ko"]="집라인을 옮기기 전에 밧줄을 먼저 분리하세요.", ["pl"]="Odłącz linę przed przeniesieniem tyrolki." },

            ["group"] = new Dictionary<string, string> {
                ["en"]="Items on top only stay on the same surface type.",
                ["ru"]="Предметы сверху сохраняются только на той же поверхности.",
                ["de"]="Gegenstände darauf bleiben nur auf demselben Untergrundtyp erhalten.",
                ["fr"]="Les objets posés dessus ne sont conservés que sur le même type de surface.",
                ["it"]="Gli oggetti sopra restano solo sullo stesso tipo di superficie.",
                ["pl"]="Przedmioty na górze zostają tylko na tym samym typie powierzchni.",
                ["pt"]="Os itens em cima só permanecem no mesmo tipo de superfície.",
                ["sv"]="Föremål ovanpå behålls bara på samma typ av yta.",
                ["zh"]="上面的物品只有在相同表面类型上才能保留。",
                ["ja"]="上に載った物は同じ設置面タイプでのみ保持されます。",
                ["ko"]="위에 놓인 물건은 같은 표면 유형에서만 유지됩니다.",
            },
            ["surface"] = new Dictionary<string, string> {
                ["en"]="It only keeps its contents on the same surface type.",
                ["ru"]="Содержимое сохраняется только на той же поверхности.",
                ["de"]="Der Inhalt bleibt nur auf demselben Untergrundtyp erhalten.",
                ["fr"]="Le contenu n'est conservé que sur le même type de surface.",
                ["sv"]="Innehållet behålls bara på samma typ av yta.",
                ["it"]="Mantiene il contenuto solo sullo stesso tipo di superficie.",
                ["pt"]="O conteúdo só é mantido no mesmo tipo de superfície.",
                ["zh"]="只有放在相同类型的表面上才会保留内容物。",
                ["ja"]="同じ種類の面に置いた場合のみ中身が保持されます。",
                ["ko"]="같은 종류의 표면에서만 내용물이 유지됩니다.", ["pl"]="Zachowuje zawartość tylko na tym samym typie powierzchni." },

            ["no_host"] = new Dictionary<string, string> {
                ["en"]="Couldn't reach the host. Left where it was.",
                ["ru"]="Не удалось связаться с хостом. Оставлено на месте.",
                ["de"]="Host nicht erreichbar. Bleibt, wo es war.",
                ["fr"]="Impossible de joindre l'hôte. Laissé sur place.",
                ["sv"]="Kunde inte nå värden. Lämnades där det var.",
                ["it"]="Impossibile raggiungere l'host. Lasciato dov'era.",
                ["pt"]="Não foi possível contatar o host. Deixado no lugar.",
                ["zh"]="无法连接主机，已留在原处。",
                ["ja"]="ホストに接続できませんでした。元の場所のままです。",
                ["ko"]="호스트에 연결할 수 없습니다. 원래 자리에 두었습니다.", ["pl"]="Nie udało się połączyć z hostem. Pozostawiono na miejscu." },

            ["working"] = new Dictionary<string, string> {
                ["en"]="Still working on it…", ["ru"]="Ещё работаю…", ["de"]="Wird noch bearbeitet…",
                ["fr"]="Traitement en cours…", ["sv"]="Arbetar fortfarande på det…",
                ["it"]="Ci sto ancora lavorando…", ["pt"]="Ainda processando…",
                ["zh"]="仍在处理中…", ["ja"]="処理中です…", ["ko"]="아직 처리 중입니다…", ["pl"]="Wciąż przetwarzam…" },

            ["no_support"] = new Dictionary<string, string> {
                ["en"]="That spot has no support. Left where it was.",
                ["ru"]="Здесь нет опоры. Оставлено на месте.",
                ["de"]="An dieser Stelle gibt es keinen Halt. Bleibt, wo es war.",
                ["fr"]="Cet endroit n'a aucun support. Laissé sur place.",
                ["sv"]="Platsen saknar stöd. Lämnades där det var.",
                ["it"]="Quel punto non ha sostegno. Lasciato dov'era.",
                ["pt"]="Esse local não tem suporte. Deixado no lugar.",
                ["zh"]="该位置没有支撑，已留在原处。",
                ["ja"]="その場所には支えがありません。元の場所のままです。",
                ["ko"]="그 자리에는 지지대가 없습니다. 원래 자리에 두었습니다.", ["pl"]="To miejsce nie ma podparcia. Pozostawiono na miejscu." },

            ["no_request"] = new Dictionary<string, string> {
                ["en"]="The host didn't get the request. Left where it was.",
                ["ru"]="Хост не получил запрос. Оставлено на месте.",
                ["de"]="Der Host hat die Anfrage nicht erhalten. Bleibt, wo es war.",
                ["fr"]="L'hôte n'a pas reçu la demande. Laissé sur place.",
                ["sv"]="Värden fick inte begäran. Lämnades där det var.",
                ["it"]="L'host non ha ricevuto la richiesta. Lasciato dov'era.",
                ["pt"]="O host não recebeu o pedido. Deixado no lugar.",
                ["zh"]="主机未收到请求，已留在原处。",
                ["ja"]="ホストがリクエストを受け取りませんでした。元の場所のままです。",
                ["ko"]="호스트가 요청을 받지 못했습니다. 원래 자리에 두었습니다.", ["pl"]="Host nie otrzymał żądania. Pozostawiono na miejscu." },

            ["no_answer"] = new Dictionary<string, string> {
                ["en"]="No answer from the host. Left where it was.",
                ["ru"]="Хост не ответил. Оставлено на месте.",
                ["de"]="Keine Antwort vom Host. Bleibt, wo es war.",
                ["fr"]="Aucune réponse de l'hôte. Laissé sur place.",
                ["sv"]="Inget svar från värden. Lämnades där det var.",
                ["it"]="Nessuna risposta dall'host. Lasciato dov'era.",
                ["pt"]="Sem resposta do host. Deixado no lugar.",
                ["zh"]="主机没有响应，已留在原处。",
                ["ja"]="ホストから応答がありません。元の場所のままです。",
                ["ko"]="호스트에서 응답이 없습니다. 원래 자리에 두었습니다.", ["pl"]="Brak odpowiedzi od hosta. Pozostawiono na miejscu." },

            ["r_not_found"] = new Dictionary<string, string> {
                ["en"]="The host couldn't find that block.",
                ["ru"]="Хост не нашёл этот блок.",
                ["de"]="Der Host konnte diesen Block nicht finden.",
                ["fr"]="L'hôte n'a pas trouvé ce bloc.",
                ["sv"]="Värden kunde inte hitta blocket.",
                ["it"]="L'host non ha trovato quel blocco.",
                ["pt"]="O host não encontrou esse bloco.",
                ["zh"]="主机找不到该建筑块。",
                ["ja"]="ホストがそのブロックを見つけられませんでした。",
                ["ko"]="호스트가 해당 블록을 찾지 못했습니다.", ["pl"]="Host nie znalazł tego bloku." },

            ["r_no_rebuild"] = new Dictionary<string, string> {
                ["en"]="That block can't be rebuilt on the host.",
                ["ru"]="Этот блок нельзя воссоздать на хосте.",
                ["de"]="Dieser Block kann auf dem Host nicht neu erstellt werden.",
                ["fr"]="Ce bloc ne peut pas être recréé sur l'hôte.",
                ["sv"]="Blocket kan inte återskapas på värden.",
                ["it"]="Quel blocco non può essere ricreato sull'host.",
                ["pt"]="Esse bloco não pode ser recriado no host.",
                ["zh"]="该建筑块无法在主机上重建。",
                ["ja"]="そのブロックはホスト側で再作成できません。",
                ["ko"]="해당 블록은 호스트에서 다시 만들 수 없습니다.", ["pl"]="Tego bloku nie można odtworzyć na hoście." },

            ["r_not_ready"] = new Dictionary<string, string> {
                ["en"]="The host isn't ready. Try again in a moment.",
                ["ru"]="Хост не готов. Попробуйте через секунду.",
                ["de"]="Der Host ist nicht bereit. Versuche es gleich erneut.",
                ["fr"]="L'hôte n'est pas prêt. Réessayez dans un instant.",
                ["sv"]="Värden är inte redo. Försök igen om ett ögonblick.",
                ["it"]="L'host non è pronto. Riprova tra un istante.",
                ["pt"]="O host não está pronto. Tente novamente em um instante.",
                ["zh"]="主机尚未就绪，请稍后再试。",
                ["ja"]="ホストの準備ができていません。少し待ってからお試しください。",
                ["ko"]="호스트가 준비되지 않았습니다. 잠시 후 다시 시도하세요.", ["pl"]="Host nie jest gotowy. Spróbuj ponownie za chwilę." },

            ["r_no_place"] = new Dictionary<string, string> {
                ["en"]="The host couldn't place it there.",
                ["ru"]="Хост не смог поставить это туда.",
                ["de"]="Der Host konnte es dort nicht platzieren.",
                ["fr"]="L'hôte n'a pas pu le placer là.",
                ["sv"]="Värden kunde inte placera det där.",
                ["it"]="L'host non è riuscito a posizionarlo lì.",
                ["pt"]="O host não conseguiu colocá-lo ali.",
                ["zh"]="主机无法将其放置在那里。",
                ["ja"]="ホストがそこに設置できませんでした。",
                ["ko"]="호스트가 거기에 설치하지 못했습니다.", ["pl"]="Host nie mógł tego tam umieścić." },

            ["r_move_failed"] = new Dictionary<string, string> {
                ["en"]="The move failed on the host. Left where it was.",
                ["ru"]="Перемещение не удалось на хосте. Оставлено на месте.",
                ["de"]="Die Bewegung ist auf dem Host fehlgeschlagen. Bleibt, wo es war.",
                ["fr"]="Le déplacement a échoué sur l'hôte. Laissé sur place.",
                ["sv"]="Flytten misslyckades på värden. Lämnades där det var.",
                ["it"]="Lo spostamento è fallito sull'host. Lasciato dov'era.",
                ["pt"]="O movimento falhou no host. Deixado no lugar.",
                ["zh"]="在主机上移动失败，已留在原处。",
                ["ja"]="ホスト側で移動に失敗しました。元の場所のままです。",
                ["ko"]="호스트에서 이동에 실패했습니다. 원래 자리에 두었습니다.", ["pl"]="Przenoszenie nie powiodło się na hoście. Pozostawiono na miejscu." },
        };
    }
}
