const triggers = new WeakMap();
export function open(dialog, reference) {
    triggers.set(dialog, document.activeElement);
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        reference.invokeMethodAsync('CloseAsync');
    });
    dialog.showModal();
}
export function close(dialog) {
    dialog.close();
    const trigger = triggers.get(dialog);
    if (trigger?.isConnected) trigger.focus();
    triggers.delete(dialog);
}
