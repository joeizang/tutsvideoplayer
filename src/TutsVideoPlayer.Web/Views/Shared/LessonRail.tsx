import { useState } from "react";
import type { CourseTreeModel, CourseTreeNodeModel } from "dotnet:types/TutsVideoPlayer/Web/Models";

export function LessonRail({ rail, currentLessonId }: { rail: CourseTreeModel; currentLessonId: string }) {
    const [collapsed, setCollapsed] = useState<Set<string>>(new Set());

    const childrenByFolder = new Map<string | null, CourseTreeNodeModel[]>();
    for (const node of rail.nodes) {
        const key = node.folderId ?? null;
        const siblings = childrenByFolder.get(key) ?? [];
        siblings.push(node);
        childrenByFolder.set(key, siblings);
    }

    const toggle = (folderId: string) => {
        setCollapsed((current) => {
            const next = new Set(current);
            if (next.has(folderId)) {
                next.delete(folderId);
            } else {
                next.add(folderId);
            }
            return next;
        });
    };

    const renderNodes = (folderId: string | null, depth: number) => {
        const nodes = childrenByFolder.get(folderId) ?? [];
        return (
            <ul className="m-0 list-none p-0" role={depth === 0 ? "tree" : "group"}>
                {nodes.map((node) => {
                    if (node.type === "folder") {
                        const isCollapsed = collapsed.has(node.id);
                        return (
                            <li key={node.id} role="none" className="mt-2">
                                <button
                                    type="button"
                                    className="inline-flex items-center gap-1 border-0 bg-transparent p-0 text-left text-sm font-semibold text-ink dark:text-neutral-100"
                                    aria-expanded={!isCollapsed}
                                    onClick={() => toggle(node.id)}
                                >
                                    <span aria-hidden="true" className="w-4 text-ink-soft dark:text-neutral-400">{isCollapsed ? "▸" : "▾"}</span>
                                    {node.title}
                                </button>
                                {!isCollapsed ? renderNodes(node.id, depth + 1) : null}
                            </li>
                        );
                    }

                    const isCurrent = node.id === currentLessonId;
                    return (
                        <li key={node.id} role="none" className="leading-snug">
                            <a
                                href={`/watch/${node.id}`}
                                role="treeitem"
                                aria-current={isCurrent ? "true" : undefined}
                                className={`-mx-1 my-0.5 inline-block rounded px-1.5 py-0.5 text-sm ${
                                    isCurrent
                                        ? "bg-action text-white"
                                        : "text-ink hover:bg-action/10 dark:text-neutral-200"
                                }`}
                            >
                                {!node.available ? (
                                    <span className="text-danger dark:text-red-400">missing · </span>
                                ) : null}
                                {node.completed && node.available ? (
                                    <span aria-label="completed" className="text-completion dark:text-emerald-400">✓ </span>
                                ) : null}
                                <span className={node.completed && node.available ? "text-ink-soft dark:text-neutral-400" : undefined}>
                                    {node.title}
                                </span>
                            </a>
                        </li>
                    );
                })}
            </ul>
        );
    };

    return (
        <nav className="w-full shrink-0 overflow-y-auto border-b border-ink/10 bg-surface p-4 lg:h-auto lg:w-[300px] lg:border-b-0 lg:border-r dark:border-neutral-700 dark:bg-neutral-800" aria-label="Course lessons">
            <h2 className="m-0 mb-3 text-base font-semibold">{rail.courseTitle}</h2>
            {!rail.available ? <p className="text-xs text-danger dark:text-red-400">This course is currently unavailable.</p> : null}
            {renderNodes(null, 0)}
        </nav>
    );
}