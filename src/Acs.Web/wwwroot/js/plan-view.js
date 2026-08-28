// Přibližování a posun plánu patra.
//
// Patro budovy MOC má i přes dvě stě místností. Do jedné obrazovky se čitelně
// nevejdou, proto se popisky zobrazují podle měřítka: při pohledu na celé patro
// je vidět jen struktura, po přiblížení se dopisují čísla místností a kódy
// čteček. Kroužky čteček a texty se dopočítávají tak, aby na obrazovce zůstaly
// pořád stejně velké — jinak by z nich po přiblížení byly kotouče.
(() => {
    /// Nejmenší velikost popisku na obrazovce, při které má smysl ho kreslit (px).
    const MIN_LABEL_PX = 7;
    const MIN_ZOOM = 1;
    const MAX_ZOOM = 40;

    /// <param name="svg">Element svg s viewBox 0 0 100 100.</param>
    /// <param name="content">Skupina, která se posouvá a přibližuje.</param>
    window.createPlanView = function (svg, content, options) {
        const opts = options || {};
        const view = { k: 1, x: 0, y: 0 };
        let pan = null;
        let queued = false;

        function transform() {
            content.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.k})`);
        }

        function clamp() {
            view.k = Math.min(Math.max(view.k, MIN_ZOOM), MAX_ZOOM);
            // Plán nesmí uplavat mimo plochu — vždy zůstane vidět.
            const limit = 100 * (view.k - 1);
            view.x = Math.min(0, Math.max(-limit, view.x));
            view.y = Math.min(0, Math.max(-limit, view.y));
        }

        /// Přepočte prvky, jejichž velikost nemá se přiblížením růst.
        function rescale() {
            const inverse = (1 / view.k).toFixed(4);
            for (const marker of content.querySelectorAll('[data-plan-marker]')) {
                const [x, y] = marker.getAttribute('data-plan-marker').split(',');
                marker.setAttribute('transform', `translate(${x} ${y}) scale(${inverse})`);
            }

            // Kolik pixelů je jedna jednotka plánu na svislé ose.
            const pxPerUnit = (svg.clientHeight || 400) / 100;
            for (const label of content.querySelectorAll('[data-plan-label]')) {
                const size = parseFloat(label.getAttribute('data-plan-label'));
                const minZoom = parseFloat(label.getAttribute('data-plan-min-zoom') || '0');
                const onScreen = label.hasAttribute('data-plan-marker-label')
                    ? size * pxPerUnit
                    : size * view.k * pxPerUnit;
                const visible = view.k >= minZoom && onScreen >= MIN_LABEL_PX;
                label.style.display = visible ? '' : 'none';
            }
        }

        function apply() {
            clamp();
            transform();
            if (queued) return;
            queued = true;
            requestAnimationFrame(() => {
                queued = false;
                rescale();
                if (opts.onChange) opts.onChange(view.k);
            });
        }

        /// Souřadnice ukazatele v jednotkách plánu (0–100), se zahrnutím přiblížení.
        function toPlan(clientX, clientY) {
            const point = svg.createSVGPoint();
            point.x = clientX;
            point.y = clientY;
            return point.matrixTransform(content.getScreenCTM().inverse());
        }

        function zoomAt(clientX, clientY, factor) {
            const before = toPlan(clientX, clientY);
            view.k = Math.min(Math.max(view.k * factor, MIN_ZOOM), MAX_ZOOM);
            transform();
            const after = toPlan(clientX, clientY);
            // Bod pod ukazatelem musí zůstat na místě.
            view.x += (after.x - before.x) * view.k;
            view.y += (after.y - before.y) * view.k;
            apply();
        }

        svg.addEventListener('wheel', evt => {
            evt.preventDefault();
            zoomAt(evt.clientX, evt.clientY, evt.deltaY < 0 ? 1.2 : 1 / 1.2);
        }, { passive: false });

        // Posouvat se dá tažením podkladu; tažení prvku si řeší editor sám.
        svg.addEventListener('pointerdown', evt => {
            if (evt.target !== svg && !evt.target.hasAttribute('data-plan-background'))
                return;

            pan = { x: evt.clientX, y: evt.clientY };
            svg.style.cursor = 'grabbing';
        });

        svg.addEventListener('pointermove', evt => {
            if (!pan) return;
            const unitX = 100 / (svg.clientWidth || 400);
            const unitY = 100 / (svg.clientHeight || 300);
            view.x += (evt.clientX - pan.x) * unitX;
            view.y += (evt.clientY - pan.y) * unitY;
            pan = { x: evt.clientX, y: evt.clientY };
            apply();
        });

        function endPan() {
            pan = null;
            svg.style.cursor = '';
        }

        svg.addEventListener('pointerup', endPan);
        svg.addEventListener('pointerleave', endPan);

        return {
            get zoom() { return view.k; },
            toPlan,
            refresh: apply,
            zoomBy(factor) {
                const box = svg.getBoundingClientRect();
                zoomAt(box.left + box.width / 2, box.top + box.height / 2, factor);
            },
            reset() {
                view.k = 1;
                view.x = 0;
                view.y = 0;
                apply();
            },
        };
    };

    /// Popisek místnosti: na plán se vejde jen číslo, celý název zůstává v bublině.
    window.planShortLabel = function (name) {
        return name.split('\u2014')[0].trim() || name;
    };
})();
