// Textové hledání zaměstnance nad nativním <select> (Nová žádost, Žádost o parkovací povolení).
// Hledá bez diakritiky a velikosti písmen („pavlicek“ najde „Pavlíček“), po více slovech
// (jméno + oddělení). Možnosti výběru se neschovávají atributem hidden (Safari ho u <option>
// ignoruje) — výběr se za psaní znovu sestaví jen z odpovídajících položek a první se rovnou vybere.
//
// Použití: <input id="X-search"> + <select id="X-select"> s <option data-search="…">,
// volitelně <p id="X-hint">. Inicializace: window.acsEmployeeSearch('X').
(() => {
    const fold = (text) => (text || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
    const matches = (haystack, term) => term.split(/\s+/).filter(Boolean).every(word => haystack.includes(word));

    window.acsEmployeeSearch = (prefix) => {
        const search = document.getElementById(`${prefix}-search`);
        const select = document.getElementById(`${prefix}-select`);
        const hint = document.getElementById(`${prefix}-hint`);
        if (!search || !select) return;

        const all = [...select.querySelectorAll('option[data-search]')].map(option => ({
            value: option.value,
            label: option.textContent.trim(),
            search: fold(option.dataset.search),
        }));
        const placeholder = select.querySelector('option[value=""]');
        const hintText = hint ? hint.textContent : '';

        const rebuild = (term) => {
            const folded = fold(term.trim());
            const found = folded === '' ? all : all.filter(item => matches(item.search, folded));
            const current = select.value;
            select.innerHTML = '';
            if (placeholder && (folded === '' || found.length === 0)) select.appendChild(placeholder.cloneNode(true));
            for (const item of found.slice(0, 300)) {
                const option = document.createElement('option');
                option.value = item.value;
                option.textContent = item.label;
                select.appendChild(option);
            }
            // Zůstane vybraný ten, kdo hledání odpovídá; jinak první nalezený.
            if (found.some(item => item.value === current) && current !== '') select.value = current;
            else if (folded !== '' && found.length > 0) select.value = found[0].value;
            select.size = folded !== '' && found.length > 1 ? Math.min(found.length, 8) : 1;
            select.dispatchEvent(new Event('change', { bubbles: true }));
            if (hint) {
                hint.textContent = folded === ''
                    ? hintText
                    : found.length === 0
                        ? `Nikdo neodpovídá „${term.trim()}“ — zkuste část jména, oddělení nebo osobní číslo.`
                        : `${found.length} odpovídá${found.length > 300 ? ' (zobrazeno 300)' : ''}.`;
            }
        };

        search.addEventListener('input', () => rebuild(search.value));
        // Enter v hledání nemá odeslat formulář, jen přesunout se na výběr.
        search.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') { event.preventDefault(); select.focus(); }
        });
    };
})();
