(() => {
    const menuSelector = 'details[data-action-menu]';

    function closeMenus(except = null) {
        document.querySelectorAll(`${menuSelector}[open]`).forEach((menu) => {
            if (menu !== except) {
                menu.removeAttribute('open');
            }
        });
    }

    document.addEventListener('click', (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const menu = target?.closest(menuSelector);

        if (target?.closest('[data-menu-action]')) {
            closeMenus();
            return;
        }

        closeMenus(menu);
    });

    document.addEventListener('keydown', (event) => {
        if (event.key !== 'Escape') {
            return;
        }

        const openMenu = document.querySelector(`${menuSelector}[open]`);
        if (!openMenu) {
            return;
        }

        openMenu.removeAttribute('open');
        openMenu.querySelector('summary')?.focus();
    });
})();
