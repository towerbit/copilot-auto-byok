(() => {
    const expandedTags = new Set(['Anthropic', 'OpenAI']);
    let initialized = false;

    const expandTargetTags = () => {
        if (initialized) return;

        const targetSections = Array.from(document.querySelectorAll('.opblock-tag-section'))
            .map(section => ({
                section,
                heading: section.querySelector('.opblock-tag'),
                name: section.querySelector('.opblock-tag span')?.textContent?.trim()
            }))
            .filter(item => expandedTags.has(item.name));

        if (targetSections.length !== expandedTags.size) return;

        targetSections.forEach(({ section, heading }) => {
            if (heading && !section.querySelector('.operation-tag-content')) {
                heading.click();
            }
        });

        initialized = true;
        observer.disconnect();
    };

    const observer = new MutationObserver(expandTargetTags);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    expandTargetTags();
})();
