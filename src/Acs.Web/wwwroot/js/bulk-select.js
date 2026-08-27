// Zaškrtávání řádků pro hromadné akce: „označit vše“, Shift+klik na rozsah
// a průběžný počet vybraných na tlačítku. Sdílí ho seznam čteček i skupin.
(() => {
    const all = document.getElementById('check-all');
    const rows = [...document.querySelectorAll('.row-check')];
    const count = document.getElementById('bulk-count');
    const assign = document.getElementById('bulk-assign');
    if (!all || rows.length === 0) return;

    let last = null;

    function refresh() {
        const selected = rows.filter(r => r.checked).length;
        if (count) count.textContent = selected;
        if (assign) assign.disabled = selected === 0;
        all.checked = selected > 0 && selected === rows.length;
        all.indeterminate = selected > 0 && selected < rows.length;
    }

    all.addEventListener('change', () => {
        for (const row of rows) row.checked = all.checked;
        refresh();
    });

    rows.forEach((row, index) => {
        row.addEventListener('click', e => {
            // Shift+klik označí celý rozsah — u stovek řádků je to rychlejší než klikat po jedné.
            if (e.shiftKey && last !== null) {
                const [from, to] = index < last ? [index, last] : [last, index];
                for (let i = from; i <= to; i++) rows[i].checked = row.checked;
            }
            last = index;
            refresh();
        });
    });

    refresh();
})();
