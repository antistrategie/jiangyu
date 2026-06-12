// Mounted-modal stack so stacked dialogs behave: only the topmost modal
// handles Escape and traps Tab, and app-level Escape shortcuts can check
// `isModalOpen()` to stand down while any dialog is up.

const modalStack: symbol[] = [];

export function pushModal(token: symbol): void {
  modalStack.push(token);
}

export function popModal(token: symbol): void {
  const i = modalStack.indexOf(token);
  if (i !== -1) modalStack.splice(i, 1);
}

export function isTopmostModal(token: symbol): boolean {
  return modalStack[modalStack.length - 1] === token;
}

export function isModalOpen(): boolean {
  return modalStack.length > 0;
}
