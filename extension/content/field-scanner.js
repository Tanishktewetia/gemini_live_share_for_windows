(() => {
  const fieldIdAttribute = "data-gemini-live-share-field-id";
  let nextFieldId = 1;
  const fields = [];
  const seen = new Set();

  function isPasswordField(element) {
    return element instanceof HTMLInputElement && element.type.toLowerCase() === "password";
  }

  function isLabelable(element) {
    return element instanceof HTMLInputElement ||
      element instanceof HTMLSelectElement ||
      element instanceof HTMLTextAreaElement ||
      element instanceof HTMLButtonElement;
  }

  function getLabel(element) {
    if (element.id) {
      const explicitLabel = document.querySelector(`label[for="${CSS.escape(element.id)}"]`);
      if (explicitLabel?.textContent?.trim()) {
        return explicitLabel.textContent.trim();
      }
    }

    const wrappedLabel = element.closest("label");
    if (wrappedLabel?.textContent?.trim()) {
      return wrappedLabel.textContent.trim();
    }

    return element.getAttribute("aria-label")?.trim() || "";
  }

  function getType(element) {
    if (element instanceof HTMLInputElement) {
      return element.type.toLowerCase();
    }

    if (element instanceof HTMLTextAreaElement) {
      return "textarea";
    }

    if (element instanceof HTMLSelectElement) {
      return "select";
    }

    return "button";
  }

  function getValue(element) {
    if (element instanceof HTMLInputElement && element.type.toLowerCase() === "file") {
      return "";
    }

    return "value" in element ? String(element.value ?? "") : "";
  }

  function scanElement(element) {
    if (!isLabelable(element) || seen.has(element) || isPasswordField(element)) {
      return;
    }

    seen.add(element);
    let id = element.getAttribute(fieldIdAttribute);
    if (!id) {
      id = `field-${nextFieldId++}`;
      element.setAttribute(fieldIdAttribute, id);
    }

    fields.push({
      id,
      label: getLabel(element),
      type: getType(element),
      required: element.hasAttribute("required") || element.getAttribute("aria-required") === "true",
      value: getValue(element)
    });
  }

  for (const form of document.forms) {
    for (const element of form.elements) {
      scanElement(element);
    }
  }

  for (const element of document.querySelectorAll("input, select, textarea, button")) {
    scanElement(element);
  }

  const iframeCount = document.querySelectorAll("iframe").length;
  return {
    url: location.href,
    title: document.title || null,
    fields,
    notices: iframeCount > 0 ? ["Iframe forms not yet supported."] : []
  };
})();
