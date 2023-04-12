![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/VSIntelliSenseTweaks/logo.png)

# VSIntelliSenseTweaks

Features:
  - Modified filtering algorithm that improves matching between your typed text and available items.
    - Items are scored based on how well they matched the typed text.
    - Not as strict as default algorithm, as long as all typed characters appears somewhere in correct order in the word it will match.
    - Experience less 'no suggestions'.

    Default filtering | VSIntelliSenseTweaks filtering
    -|-
    ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/default_verygoodnameindeed.png) | ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/tweaked_verygoodnameindeed.png)
    ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/default_veryGoodNameIndeed1.png) | ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/tweaked_veryGoodNameIndeed1.png)
    
  - Multi caret/selection intelliSense support.
    - Place two or more carets and trigger word completion (currently hard coded to ctrl + space) and use intelliSense like you are used to in other editors such as VS code.
    
    ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/multiCaretIntelliSense.gif) | ![](https://github.com/cfognom/VSIntelliSenseTweaks/blob/master/Media/multiCaretIntelliSense1.gif)
    -|-
