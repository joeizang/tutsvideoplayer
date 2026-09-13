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
            <ul className="rail-list" role={depth === 0 ? "tree" : "group"}>
                {nodes.map((node) => {
                    if (node.type === "folder") {
                        const isCollapsed = collapsed.has(node.id);
                        return (
                            <li key={node.id} role="none">
                                <button
                                    type="button"
                                    className="rail-folder"
                                    aria-expanded={!isCollapsed}
                                    onClick={() => toggle(node.id)}
                                >
                                    <span aria-hidden="true">{isCollapsed ? "▸" : "▾"}</span> {node.title}
                                </button>
                                {!isCollapsed ? renderNodes(node.id, depth + 1) : null}
                            </li>
                        );
                    }

                    const isCurrent = node.id === currentLessonId;
                    return (
                        <li key={node.id} role="none" className={isCurrent ? "rail-current" : undefined}>
                            <a
                                href={`/watch/${node.id}`}
                                role="treeitem"
                                aria-current={isCurrent ? "true" : undefined}
                                aria-label={`${node.title}${node.available ? "" : " (missing)"}`}
                            >
                                {!node.available ? <span className="rail-missing">missing · </span> : null}
                                {node.title}
                            </a>
                        </li>
                    );
                })}
            </ul>
        );
    };

    return (
        <nav className="lesson-rail" aria-label="Course lessons">
            <h2 className="rail-title">{rail.courseTitle}</h2>
            {!rail.available ? <p className="rail-missing-note">This course is currently unavailable.</p> : null}
            {renderNodes(null, 0)}
        </nav>
    );
}
