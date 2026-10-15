# Material Design 3

Интерфейс использует базовую фиолетовую схему MD3 и семантические цветовые роли.
`Theming/M3Theme.cs` задаёт светлые и тёмные значения; `Theming/M3Controls.xaml`
содержит типографику и стили компонентов. Цвета переключаются через DynamicResource.

## Цвет и типографика

- Фон: Surface / OnSurface. Вторичный текст: OnSurfaceVariant, без уменьшения прозрачности.
- Filled card: SurfaceContainerHighest / OnSurface. Outlined card: Surface / OutlineVariant.
- Основное действие: Primary / OnPrimary. Дополнительные действия: SecondaryContainer / OnSecondaryContainer.
- Ошибки: ErrorContainer / OnErrorContainer. Snackbar: InverseSurface / InverseOnSurface.
- Success и Warning — дополнительные семантические роли для состояния службы, не стандартные роли MD3.
- Встроенный Roboto: headline small 24/32 regular, title medium 16/24 medium,
  body medium 14/20 regular, label medium 12/16 medium, button label 14 medium.

## Компоненты

- Кнопки: круглая форма, визуальная высота 40 DIP внутри области взаимодействия 48 DIP;
  варианты filled, tonal, outlined и text. Hover 8%, focus/pressed 10%, видимый keyboard focus ring.
- Карточки: радиус 12 DIP, внутренние отступы 16 DIP, расстояние 8 DIP.
- Основные вкладки с иконками: высота 64 DIP, активный индикатор 3 DIP, divider 1 DIP.
- Outlined-поля: высота 56 DIP, радиус 4 DIP, внутренняя горизонтальная область 16 DIP.
- Switch: track 52×32 DIP, outline 2 DIP, handle 16 DIP в выключенном состоянии
  и 24 DIP с иконкой в выбранном состоянии, область взаимодействия высотой 48 DIP.
- Удаление службы использует outlined-кнопку с Error-текстом и меньшим визуальным акцентом.

У MaterialDesignThemes 5.2.1 словарь MaterialDesign3.Defaults оставляет ряд компонентов MD2,
поэтому одного подключения словаря недостаточно. Стили приложения заменяют форму кнопок,
навигацию и switch, сохраняя стандартные WPF-контролы, команды и доступность с клавиатуры.

## Источники

- [Color roles](https://m3.material.io/styles/color/roles)
- [Typography](https://m3.material.io/styles/typography/type-scale-tokens)
- [Buttons](https://m3.material.io/components/buttons/specs)
- [Cards](https://m3.material.io/components/cards/specs)
- [Tabs](https://m3.material.io/components/tabs/specs)
- [Text fields](https://m3.material.io/components/text-fields/specs)
- [Switch](https://m3.material.io/components/switch/specs)

Проверки WPF загружают обе вкладки, контролируют ошибки binding и контраст основных
текстовых пар (не ниже 4.5:1), сохраняют PNG для визуального осмотра. Параметры
`--dark`, `--lists`, `--small`, `--error`, `--running` выбирают тему и сценарий.
