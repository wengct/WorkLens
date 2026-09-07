export function initialize(container) {
    const markers = [...container.querySelectorAll('.sensitive-marker')];
    const previous = container.querySelector('[data-sensitive-previous]');
    const next = container.querySelector('[data-sensitive-next]');
    const position = container.querySelector('[data-sensitive-position]');
    let current = -1;

    function navigate(index) {
        if (!markers.length) return;
        if (current >= 0) {
            markers[current].classList.remove('is-current');
            markers[current].removeAttribute('aria-current');
        }
        current = (index + markers.length) % markers.length;
        const marker = markers[current];
        const section = marker.closest('details');
        section.open = true;
        marker.classList.add('is-current');
        marker.setAttribute('aria-current', 'true');
        position.textContent = `${current + 1} / ${markers.length} 處 · ${section.querySelector('summary').textContent} · ${marker.textContent}`;
        marker.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
    }

    previous.disabled = next.disabled = markers.length === 0;
    position.textContent = '預覽中沒有遮蔽標記';
    previous.addEventListener('click', () => navigate(current - 1));
    next.addEventListener('click', () => navigate(current + 1));
    container.addEventListener('keydown', event => {
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
        event.preventDefault();
        navigate(current + (event.key === 'ArrowUp' ? -1 : 1));
    });
    container.addEventListener('click', event => {
        const index = markers.indexOf(event.target);
        if (index >= 0) navigate(index);
    });
    container.focus({ preventScroll: true });
    navigate(0);
}
