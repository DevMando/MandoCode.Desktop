# Desktop controls

| Folder | Responsibility |
| --- | --- |
| Chat | ChatTabView XAML, code-behind, and all partial files |
| Notes | Note editor and AI ask bar |
| Settings | Shared settings form |
| Terminal | Integrated terminal panel |
| Common | Shared layout panels and resize/drag controls |

Keep a control's XAML, code-behind, and partial files together.
Namespaces and XAML class names are independent of these source folders and stay
unchanged so existing consumers keep resolving the same control types.
